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
        processSpecFactory: async context =>
        {
            var interactions = context.Services.GetRequiredService<IInteractionService>();
            //Always check if the service is available!!
            if (!interactions.IsAvailable)
            {
                throw new InvalidOperationException("Export catalog OpenAPI requires an interactive client. Run it from the Aspire dashboard.");
            }

            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture);
            var result = await interactions.PromptInputsAsync(
                "Export catalog OpenAPI",
                "Choose a JSON filename and destination folder on the AppHost machine. Relative folders are resolved from the AppHost directory.",
                [
                    new InteractionInput
                    {
                        Name = "fileName",
                        Label = "File name",
                        InputType = InputType.Text,
                        Required = true,
                        Value = $"catalog-openapi-{timestamp}.json"
                    },
                    new InteractionInput
                    {
                        Name = "directory",
                        Label = "Destination folder",
                        InputType = InputType.Text,
                        Required = true,
                        Value = Path.Combine(builder.AppHostDirectory, "artifacts", "openapi")
                    }
                ],
                new InputsDialogInteractionOptions
                {
                    ValidationCallback = validation =>
                    {
                        var fileName = validation.Inputs["fileName"].Value;
                        var directory = validation.Inputs["directory"].Value;
                        if (string.IsNullOrWhiteSpace(fileName)
                            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                            || fileName.Contains('/') || fileName.Contains('\\')
                            || !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        {
                            validation.AddValidationError("fileName", "Enter a valid filename ending in .json, without a folder path.");
                            return Task.CompletedTask;
                        }

                        if (string.IsNullOrWhiteSpace(directory))
                        {
                            validation.AddValidationError("directory", "Enter a destination folder.");
                            return Task.CompletedTask;
                        }

                        try
                        {
                            var fullDirectory = Path.GetFullPath(directory, builder.AppHostDirectory);
                            var fullPath = Path.Combine(fullDirectory, fileName);
                            if (File.Exists(fullDirectory))
                            {
                                validation.AddValidationError("directory", "The destination is a file, not a folder.");
                            }
                            else if (File.Exists(fullPath) || Directory.Exists(fullPath))
                            {
                                validation.AddValidationError("fileName", "This path already exists. Choose a different filename.");
                            }
                        }
                        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                        {
                            validation.AddValidationError("directory", $"Invalid destination folder: {ex.Message}");
                        }

                        return Task.CompletedTask;
                    }
                },
                context.CancellationToken);

            if (result.Canceled)
            {
                context.Logger.LogInformation("OpenAPI export canceled.");
                throw new OperationCanceledException("OpenAPI export canceled.", context.CancellationToken);
            }

            var exportDirectory = Path.GetFullPath(
                result.Data["directory"].Value ?? throw new InvalidOperationException("A destination folder is required."),
                builder.AppHostDirectory);
            var outputPath = Path.Combine(exportDirectory,
                result.Data["fileName"].Value ?? throw new InvalidOperationException("A filename is required."));
            context.CancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(outputPath) || Directory.Exists(outputPath))
            {
                throw new IOException($"The export destination already exists: {outputPath}");
            }

            Directory.CreateDirectory(exportDirectory);
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
        executeCommand: async (context) =>
        {
         var interactionService = context.Services.GetRequiredService<IInteractionService>();
         using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);

            var brandId = context.Arguments.GetInt32("brandId");
            var path = $"/api/v1/catalog/items/type/all/brand/{brandId.ToString(CultureInfo.InvariantCulture)}";

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
       
            var progressTask = interactionService.PromptProgressAsync("Processing data...",cancellationToken: cts.Token);

#pragma warning restore ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

            try
            {
                using var client = new HttpClient();
                using var response = await client.GetAsync(new Uri(new Uri(catalogEndpoint.Url), path), context.CancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return CommandResults.Failure($"Catalog service returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
                }

                var json = await response.Content.ReadAsStringAsync(context.CancellationToken);
                cts.Cancel(); // Cancel the progress prompt once the response is received
                await progressTask;
                
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
            var interactions = context.Services.GetRequiredService<IInteractionService>();
            if (!interactions.IsAvailable)
            {
                const string message = "Reset Shop State requires an interactive client. Run it from the Aspire dashboard.";
                context.Logger.LogWarning("{Message}", message);
                return CommandResults.Failure(message);
            }

            var confirmation = await interactions.PromptConfirmationAsync(
                title: "Reset Shop State",
                message: "This clears the Redis cache and deletes and recreates the catalog database. Continue?",
                options: new MessageBoxInteractionOptions
                {
                    Intent = MessageIntent.Confirmation,
                    PrimaryButtonText = "Reset",
                    SecondaryButtonText = "Cancel",
                    ShowSecondaryButton = true
                },
                cancellationToken: context.CancellationToken);

            if (confirmation.Canceled || !confirmation.Data)
            {
                context.Logger.LogInformation("Shop state reset canceled.");
                return CommandResults.Failure("Shop state reset canceled. No changes were made.");
            }

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
            IsHighlighted = true
        })
    .WithHttpHealthCheck("/health")
    .WithReference(basketService)
    .WithReference(catalogService)
    .WaitFor(catalogService);

builder.Build().Run();
