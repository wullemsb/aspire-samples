using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using CustomResources.AppHost;
using Xunit;

namespace CustomResources.Tests;

public class ScalarResourceTests
{
    [Fact]
    public async Task DiscoversAllDocumentsIncludingResourcesAddedAfterScalar()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.AddContainer("unrelated", "redis");
        var first = builder.AddContainer("first", "example")
            .WithHttpEndpoint(targetPort: 8080)
            .WithOpenApi(title: "First \"API\"");
        var scalar = builder.AddScalar();
        var second = builder.AddContainer("second", "example")
            .WithHttpsEndpoint(targetPort: 8443)
            .WithOpenApi("/swagger/public/swagger.json", "https", "Public")
            .WithOpenApi("/swagger/internal/swagger.json", "https", "Internal");

        Allocate(first.GetEndpoint("http"), 5101);
        Allocate(second.GetEndpoint("https"), 5102);

        using var config = await GetConfigurationAsync(builder, scalar);
        var sources = config.RootElement.GetProperty("sources").EnumerateArray().ToArray();

        Assert.Equal(3, sources.Length);
        Assert.Equal("First \"API\"", sources[0].GetProperty("title").GetString());
        Assert.Equal("http://localhost:5101/openapi/v1.json", sources[0].GetProperty("url").GetString());
        Assert.Equal("https://localhost:5102/swagger/public/swagger.json", sources[1].GetProperty("url").GetString());
        Assert.Equal("Internal", sources[2].GetProperty("title").GetString());
        Assert.Equal("https://localhost:5102/swagger/internal/swagger.json", sources[2].GetProperty("url").GetString());
    }

    [Fact]
    public void ScalarIsSingletonAndConfiguredAsDevContainer()
    {
        var builder = DistributedApplication.CreateBuilder();
        var scalar = builder.AddScalar(port: 8088);

        Assert.Same(scalar.Resource, builder.AddScalar().Resource);
        Assert.Single(builder.Resources.OfType<ScalarResource>());
        var endpoint = scalar.GetEndpoint("http").EndpointAnnotation;
        Assert.Equal(8080, endpoint.TargetPort);
        Assert.Equal(8088, endpoint.Port);
        var image = Assert.Single(scalar.Resource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("scalarapi/api-reference", image.Image);
        Assert.Equal("0.6.22", image.Tag);
        Assert.Contains(scalar.Resource.Annotations, annotation => annotation is ManifestPublishingCallbackAnnotation);
    }

    [Fact]
    public void PublishDoesNotAddScalarToModel()
    {
        var builder = DistributedApplication.CreateBuilder(new[] { "--operation", "publish" });

        var scalar = builder.AddScalar();

        Assert.True(builder.ExecutionContext.IsPublishMode);
        Assert.DoesNotContain(scalar.Resource, builder.Resources);
    }

    [Fact]
    public async Task DefaultsDocumentTitleToResourceName()
    {
        var builder = DistributedApplication.CreateBuilder();
        var api = builder.AddContainer("api", "example").WithHttpEndpoint(targetPort: 8080).WithOpenApi();
        Allocate(api.GetEndpoint("http"), 5100);

        using var config = await GetConfigurationAsync(builder, builder.AddScalar());

        Assert.Equal("api", config.RootElement.GetProperty("sources")[0].GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("openapi/v1.json")]
    [InlineData("https://example.com/openapi.json")]
    [InlineData("//example.com/openapi.json")]
    [InlineData("/\\example.com/openapi.json")]
    public void RejectsPathsThatAreNotLocalAbsoluteUrlPaths(string path)
    {
        var builder = DistributedApplication.CreateBuilder();
        var api = builder.AddContainer("api", "example").WithHttpEndpoint(targetPort: 8080);

        Assert.Throws<ArgumentException>(() => api.WithOpenApi(path));
    }

    [Fact]
    public async Task ReportsMissingEndpoint()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.AddContainer("api", "example").WithOpenApi();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetConfigurationAsync(builder, builder.AddScalar()));

        Assert.Contains("existing HTTP or HTTPS endpoint 'http'", error.Message);
    }

    [Fact]
    public async Task ReportsNonHttpEndpoint()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.AddContainer("api", "example")
            .WithEndpoint(targetPort: 1234, name: "tcp")
            .WithOpenApi(endpointName: "tcp");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetConfigurationAsync(builder, builder.AddScalar()));
    }

    [Fact]
    public async Task ReportsMissingDocuments()
    {
        var builder = DistributedApplication.CreateBuilder();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetConfigurationAsync(builder, builder.AddScalar()));

        Assert.Contains("WithOpenApi()", error.Message);
    }

    [Fact]
    public async Task WaitsForEndpointAllocationAndHonorsCancellation()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.AddContainer("api", "example").WithHttpEndpoint(targetPort: 8080).WithOpenApi();
        using var cancellation = new CancellationTokenSource();
        var configuration = GetConfigurationAsync(builder, builder.AddScalar(), cancellation.Token);
        Assert.False(configuration.IsCompleted);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => configuration);
    }

    private static void Allocate(EndpointReference reference, int port)
    {
        reference.EndpointAnnotation.AllocatedEndpoint = new AllocatedEndpoint(
            reference.EndpointAnnotation, "localhost", port);
    }

    private static async Task<JsonDocument> GetConfigurationAsync(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<ScalarResource> scalar,
        CancellationToken cancellationToken = default)
    {
        var environment = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, scalar.Resource, environment, cancellationToken);
        foreach (var callback in scalar.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(context);
        }

        return JsonDocument.Parse(Assert.IsType<string>(environment["API_REFERENCE_CONFIG"]));
    }
}
