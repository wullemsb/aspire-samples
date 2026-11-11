using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace AspireShop.AppHost.CustomCommands
{
public sealed class ScalarResource(string name) : ContainerResource(name);

public sealed record OpenApiAnnotation(EndpointReference Endpoint, string Path, string Title) : IResourceAnnotation;

public sealed record ScalarEndpoint(string Title, string Url);

public sealed record ScalarEndpointsDiscoveredEvent(
    ScalarResource Resource,
    IReadOnlyList<ScalarEndpoint> Endpoints) : IDistributedApplicationEvent;

public sealed class ScalarEndpointLogger(ResourceNotificationService notification, ResourceLoggerService loggerService) : IDistributedApplicationEventingSubscriber
{
    public Task SubscribeAsync(
        IDistributedApplicationEventing eventing,
        DistributedApplicationExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        if (executionContext.IsRunMode)
        {
            eventing.Subscribe<ScalarEndpointsDiscoveredEvent>(async (@event, token) =>
            {
                var logger = loggerService.GetLogger(@event.Resource);
                logger.LogCritical("Scalar discovered {Count} OpenAPI endpoint(s).", @event.Endpoints.Count);

                foreach (var endpoint in @event.Endpoints)
                {
                    logger.LogCritical("OpenAPI endpoint {Title}: {Url}", endpoint.Title, endpoint.Url);

                    await notification.PublishUpdateAsync(@event.Resource, s => s with
                    {
                        State = new ResourceStateSnapshot("Endpoints discovered", KnownResourceStateStyles.Info)             
                    });
                }
            });
        }

        return Task.CompletedTask;
    }
}

public static class ScalarResourceBuilderExtensions
{
    /// <summary>Declares an OpenAPI document exposed by a resource for automatic Scalar discovery.</summary>
    public static IResourceBuilder<T> WithOpenApi<T>(
        this IResourceBuilder<T> builder,
        string path = "/openapi/v1.json",
        string endpointName = "http",
        string? title = null) where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);

        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\\')
            || !Uri.TryCreate(path, UriKind.Relative, out _))
        {
            throw new ArgumentException("The OpenAPI path must be an absolute URL path, such as /openapi/v1.json.", nameof(path));
        }

        if (title is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
        }

        return builder.WithAnnotation(new OpenApiAnnotation(
            builder.GetEndpoint(endpointName), path, title ?? builder.Resource.Name));
    }

    /// <summary>Adds a singleton, development-only Scalar container that discovers all declared OpenAPI documents.</summary>
    public static IResourceBuilder<ScalarResource> AddScalar(
        this IDistributedApplicationBuilder builder,
        string name = "scalar",
        int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // if (builder.ExecutionContext.IsPublishMode)
        // {
        //     return builder.CreateResourceBuilder(new ScalarResource(name));
        // }

        var existing = builder.Resources.OfType<ScalarResource>().SingleOrDefault();
        if (existing is not null)
        {
            return builder.CreateResourceBuilder(existing);
        }

        builder.Services.TryAddEventingSubscriber<ScalarEndpointLogger>();

        var scalar = new ScalarResource(name);
        return builder.AddResource(scalar)
            .WithImage("scalarapi/api-reference", "0.6.22")
            .WithImageRegistry("docker.io")
            .WithHttpEndpoint(targetPort: 8080, port: port)
            .WithHttpHealthCheck("/health")
            .WithEnvironment(async context =>
            {
                var sources = new List<ScalarEndpoint>();

                foreach (var resource in builder.Resources)
                {
                    foreach (var document in resource.Annotations.OfType<OpenApiAnnotation>())
                    {
                        if (!document.Endpoint.Exists || (!document.Endpoint.IsHttp && !document.Endpoint.IsHttps))
                        {
                            throw new InvalidOperationException(
                                $"OpenAPI document '{document.Title}' on resource '{resource.Name}' must reference an existing HTTP or HTTPS endpoint '{document.Endpoint.EndpointName}'.");
                        }

                            var browserUrl = resource.Annotations
                                .OfType<ResourceUrlAnnotation>()
                                .FirstOrDefault(url =>
                                    url.Endpoint?.EndpointName == document.Endpoint.EndpointName &&
                                    url.DisplayLocation == UrlDisplayLocation.SummaryAndDetails)
                                ?? throw new InvalidOperationException(
                                    $"No browser-facing URL found for resource '{resource.Name}'.");

        var documentUrl = new Uri(new Uri(browserUrl.Url), document.Path);
        sources.Add(new ScalarEndpoint(document.Title, documentUrl.AbsoluteUri));
                    }
                    
                }

                if (sources.Count == 0)
                {
                    throw new InvalidOperationException("Scalar requires at least one OpenAPI document. Declare one with WithOpenApi().");
                }

                context.EnvironmentVariables["API_REFERENCE_CONFIG"] =
                    JsonSerializer.Serialize(new { sources }, JsonSerializerOptions.Web);

                await context.ExecutionContext.Services
                    .GetRequiredService<IDistributedApplicationEventing>()
                    .PublishAsync(
                        new ScalarEndpointsDiscoveredEvent(scalar, sources.AsReadOnly()),
                        context.CancellationToken);
            })
            .ExcludeFromManifest();
    }

}
}