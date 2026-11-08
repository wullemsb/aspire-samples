public sealed class ScalarResource(string name) : ContainerResource(name);

public sealed record OpenApiAnnotation(EndpointReference Endpoint, string Path, string Title) : IResourceAnnotation;

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

        if (builder.ExecutionContext.IsPublishMode)
        {
            return builder.CreateResourceBuilder(new ScalarResource(name));
        }

        var existing = builder.Resources.OfType<ScalarResource>().SingleOrDefault();
        if (existing is not null)
        {
            return builder.CreateResourceBuilder(existing);
        }

        return builder.AddResource(new ScalarResource(name))
            .WithImage("scalarapi/api-reference", "0.6.22")
            .WithImageRegistry("docker.io")
            .WithHttpEndpoint(targetPort: 8080, port: port)
            .WithHttpHealthCheck("/health")
            .WithEnvironment(async context =>
            {
                var sources = new List<ScalarSource>();

                foreach (var resource in builder.Resources)
                {
                    foreach (var document in resource.Annotations.OfType<OpenApiAnnotation>())
                    {
                        if (!document.Endpoint.Exists || (!document.Endpoint.IsHttp && !document.Endpoint.IsHttps))
                        {
                            throw new InvalidOperationException(
                                $"OpenAPI document '{document.Title}' on resource '{resource.Name}' must reference an existing HTTP or HTTPS endpoint '{document.Endpoint.EndpointName}'.");
                        }

                        // Scalar fetches documents in the browser, not on the container network.
                        var endpointUrl = await document.Endpoint.GetValueAsync(context.CancellationToken)
                            ?? throw new InvalidOperationException($"The OpenAPI endpoint for resource '{resource.Name}' did not resolve to a URL.");
                        sources.Add(new ScalarSource(document.Title, $"{endpointUrl.TrimEnd('/')}{document.Path}"));
                    }
                }

                if (sources.Count == 0)
                {
                    throw new InvalidOperationException("Scalar requires at least one OpenAPI document. Declare one with WithOpenApi().");
                }

                context.EnvironmentVariables["API_REFERENCE_CONFIG"] =
                    JsonSerializer.Serialize(new { sources }, JsonSerializerOptions.Web);
            })
            .ExcludeFromManifest();
    }

    private sealed record ScalarSource(string Title, string Url);
}