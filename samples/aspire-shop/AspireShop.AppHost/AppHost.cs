using Aspire.Hosting.ApplicationModel;
using AspireShop.AppHost.CustomCommands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.Json;

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithPgAdmin()
    .WithLifetime(ContainerLifetime.Persistent);

if (builder.ExecutionContext.IsRunMode)
{
    // Data volumes don't work on ACA for Postgres so only add when running
    postgres.WithDataVolume();
}

var catalogDb = postgres.AddDatabase("catalogdb");

var basketCache = builder.AddRedis("basketcache")
    .WithDataVolume()
    .WithRedisCommander()
    // Add a custom command to clear the cache
    .WithClearCommand();
    
var catalogDbManager = builder.AddProject<Projects.AspireShop_CatalogDbManager>("catalogdbmanager")
    .WithIconName("database")
    .WithReference(catalogDb)
    .WaitFor(catalogDb)
    .WithHttpHealthCheck("/health")
    .WithHttpCommand("/reset-db", "Reset Database", commandName: "reset-db", commandOptions: new() { IconName = "DatabaseLightning", ResultMode = HttpCommandResultMode.Auto, IsHighlighted = true });

#pragma warning disable ASPIREPROCESSCOMMAND001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
var catalogService = builder.AddProject<Projects.AspireShop_CatalogService>("catalogservice")
    .WithIconName("book")
    .WithReference(catalogDb)
    .WaitFor(catalogDbManager)
    .WithHttpHealthCheck("/health")
    .WithOpenApi(endpointName: "https", title: "Catalog API")
    .WithProcessCommand(
        commandName: "dotnet-version",
        displayName: "Show .NET version",
        executablePath: "dotnet",
        arguments: ["--version"]);
#pragma warning restore ASPIREPROCESSCOMMAND001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

var catalogEndpoint = catalogService.GetEndpoint("http");

#pragma warning disable ASPIREPROCESSCOMMAND001
    catalogService.WithProcessCommand(
        commandName: "export-openapi",
        displayName: "Export catalog OpenAPI",
        processSpecFactory: context =>
        {
            var exportDirectory = Path.Combine(builder.AppHostDirectory, "artifacts", "openapi");
            Directory.CreateDirectory(exportDirectory);
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture);
            var outputPath = Path.Combine(exportDirectory, $"catalog-openapi-{timestamp}.json");
            context.Logger.LogInformation("Exporting catalog OpenAPI to {OutputPath}.", outputPath);

            return new ProcessCommandSpec("curl")
            {
                Arguments =
                [
                    "--fail",
                    "--silent",
                    "--show-error",
                    "--remove-on-error",
                    "--connect-timeout", "10",
                    "--max-time", "30",
                    "--output", outputPath,
                    "--write-out", "OpenAPI export destination: %{filename_effective}\nHTTP status: %{http_code}\n",
                    new Uri(new Uri(catalogEndpoint.Url), "/openapi/v1.json").AbsoluteUri,
                ],
            };
        },
        commandOptions: new ProcessCommandOptions
        {
            IconName = "ArrowDownload",
            IsHighlighted = true,
            UpdateState = context => context.ResourceSnapshot.HealthStatus is HealthStatus.Healthy
                ? ResourceCommandState.Enabled
                : ResourceCommandState.Disabled,
        });
#pragma warning restore ASPIREPROCESSCOMMAND001

catalogService.WithCommand(
        name: "retrieve-products",
        displayName: "Retrieve products",
        executeCommand: async context =>
        {
            var brandId = context.Arguments.GetInt32("brandId");
            var path = $"/api/v1/catalog/items/type/all/brand/{brandId.ToString(CultureInfo.InvariantCulture)}";

            try
            {
                using var client = new HttpClient();
                using var response = await client.GetAsync(new Uri(new Uri(catalogEndpoint.Url), path), context.CancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return CommandResults.Failure($"Catalog service returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
                }

                var json = await response.Content.ReadAsStringAsync(context.CancellationToken);
                return CommandResults.Success(
                    message: "Products retrieved for brand " + brandId + ".",
                    result: json,
                    resultFormat: CommandResultFormat.Json);
            }
            catch (HttpRequestException ex)
            {
                context.Logger.LogError(ex, "Failed to retrieve products for brand {BrandId}.", brandId);
                return CommandResults.Failure($"Failed to retrieve products for brand {brandId}: {ex.Message}");
            }
            catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
            {
                return CommandResults.Failure($"The catalog service request for brand {brandId} timed out.");
            }
        },
        commandOptions: new CommandOptions
        {
            Arguments =
            [
                new InteractionInput { Name = "brandId",   Label = "Brand ID",  InputType = InputType.Number, Required = true},
            ],
            ValidateArguments = context =>
            {
                var brandId = context.Inputs.GetInt32("brandId");
                if (brandId <= 0)
                {
                    context.AddValidationError("brandId", "Brand ID must be a positive number.");
                }

                return Task.CompletedTask;
            }
        });

var basketService = builder.AddProject<Projects.AspireShop_BasketService>("basketservice")
    .WithIconName("cart")
    .WithReference(basketCache)
    .WaitFor(basketCache);

builder.AddScalar()
    .WithIconName("balloon")
    .WaitFor(basketService)
    .WaitFor(catalogService);

builder.AddProject<Projects.AspireShop_Frontend>("frontend")
    .WithIconName("window")
    .WithExternalHttpEndpoints()
    .WithUrls(context =>
    {
        context.Urls.Clear();
        context.Urls.Add(new() { Url = "/", DisplayText = "Online Store", Endpoint = context.GetEndpoint("https") });
        context.Urls.Add(new() { Url = "/cart", DisplayText = "Shopping Basket", Endpoint = context.GetEndpoint("https") });
    })
    .WithCommand(
        name: "reset-shop-state",
        displayName: "Reset Shop State",
        executeCommand: async context =>
        {
            var commands = context.Services.GetRequiredService<ResourceCommandService>();
            var clearCacheResult = await commands.ExecuteCommandAsync(
                basketCache.Resource.Name,
                "clear-cache",
                context.CancellationToken);
            if (!clearCacheResult.Success)
            {
                return clearCacheResult;
            }

            return await commands.ExecuteCommandAsync(
                catalogDbManager.Resource.Name,
                "reset-db",
                context.CancellationToken);
        },
        commandOptions: new()
        {
            IconName = "ArrowReset",
            IsHighlighted = true,
            ConfirmationMessage = "This clears the Redis cache and deletes and recreates the catalog database. Continue?"
        })
    .WithHttpHealthCheck("/health")
    .WithReference(basketService)
    .WithReference(catalogService)
    .WaitFor(catalogService);

builder.Build().Run();
