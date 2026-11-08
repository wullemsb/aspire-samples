using System.Net;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Devolutions.Hosting.Tests;

public sealed class DevolutionsResourceTests
{
    private static readonly Guid VaultId = Guid.Parse("c6eca48a-0cda-4f52-94dc-d02fe0146896");
    private static readonly Guid EntryId = Guid.Parse("996aa495-f206-4a17-b7ac-82005b06e557");

    [Fact]
    public async Task RuntimeInitializationAndEnvironmentResolutionAreLazyCachedAndSensitive()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = AddServer(builder);
        var requests = new List<string>();
        builder.Services.AddHttpClient(DevolutionsApiClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new TestHttpHandler((request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                requests.Add(path);
                return Task.FromResult(TestHttpHandler.Json(path.EndsWith("/login", StringComparison.Ordinal)
                    ? """{"tokenId":"test-token"}"""
                    : path.EndsWith("/logout", StringComparison.Ordinal)
                        ? "{}"
                        : """{"payload":{"password":"test-password"}}"""));
            }));

        var secret = server.AddSecret("password", VaultId, EntryId, "/payload/password");
        var first = builder.AddContainer("first", "unused-image").WithDevolutionsSecret("PASSWORD", secret)
            .WithArgs(async context => context.Args.Add(await secret.Resource.GetParameterAsync(context.CancellationToken)));
        var second = builder.AddExecutable("second", "unused-executable", ".")
            .WithDevolutionsSecret("PASSWORD", secret)
            .WithArgs(async context => context.Args.Add(await secret.Resource.GetParameterAsync(context.CancellationToken)));
        Assert.Empty(requests);
        Assert.Null(secret.Resource.PublishParameter);
        Assert.IsAssignableFrom<IResourceWithoutLifetime>(server.Resource);
        Assert.Empty(server.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(2, server.Resource.Annotations.OfType<ResourceRelationshipAnnotation>().Count());
        Assert.Null(Assert.Single(secret.Resource.Annotations.OfType<ManifestPublishingCallbackAnnotation>()).Callback);

        await using var app = builder.Build();
        var services = app.Services;
        var eventing = services.GetRequiredService<IDistributedApplicationEventing>();
        await eventing.PublishAsync(new InitializeResourceEvent(
            server.Resource, eventing, services.GetRequiredService<ResourceLoggerService>(),
            services.GetRequiredService<ResourceNotificationService>(), services), TestContext.Current.CancellationToken);
        Assert.Empty(requests);

        var configurations = await Task.WhenAll(
            ResolveEnvironmentAsync(first.Resource, builder.ExecutionContext),
            ResolveEnvironmentAsync(second.Resource, builder.ExecutionContext));
        foreach (var configuration in configurations)
        {
            var environment = Assert.Single(configuration.EnvironmentVariables);
            Assert.Null(configuration.Exception);
            Assert.Equal("PASSWORD", environment.Key);
            Assert.Equal("test-password", environment.Value);
            Assert.True(Assert.Single(configuration.Arguments).IsSensitive);
            var parameter = Assert.IsType<ParameterResource>(
                Assert.Single(configuration.EnvironmentVariablesWithUnprocessed).Value.Unprocessed);
            Assert.True(parameter.Secret);
            Assert.DoesNotContain(builder.Resources, resource => resource == parameter);
        }
        Assert.Equal(3, requests.Count);
        Assert.Equal("/dps/api/v1/login", requests[0]);
        Assert.Equal($"/dps/api/v1/vault/{VaultId:D}/entry/{EntryId:D}", requests[1]);
        Assert.Equal("/dps/api/v1/logout", requests[2]);
        Assert.Equal("test-password", await secret.Resource.GetValueAsync(TestContext.Current.CancellationToken));
        var resolvedParameter = await secret.Resource.GetParameterAsync(TestContext.Current.CancellationToken);
        Assert.Equal("test-password", await resolvedParameter.GetValueAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, requests.Count);
        Assert.DoesNotContain("test-password", secret.Resource.ToString());
    }

    [Fact]
    public async Task FailedAndCancelledResolutionsDoNotPoisonTheCache()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = AddServer(builder);
        var attempts = 0;
        var secret = new DevolutionsSecretResource("secret", server.Resource, VaultId, EntryId, "/password", null, token =>
        {
            token.ThrowIfCancellationRequested();
            if (++attempts == 1)
            {
                throw new InvalidOperationException("Test failure");
            }
            return Task.FromResult("recovered");
        });

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secret.GetValueAsync(cancelled.Token).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => secret.GetValueAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Equal("recovered", await secret.GetValueAsync(TestContext.Current.CancellationToken));
        Assert.Equal("recovered", await secret.GetValueAsync(TestContext.Current.CancellationToken));
        var parameter = await secret.GetParameterAsync(TestContext.Current.CancellationToken);
        Assert.Equal("recovered", await parameter.GetValueAsync(TestContext.Current.CancellationToken));
        Assert.Same(parameter, await secret.GetParameterAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task PublishingUsesRequiredSecretParameterWithoutFetchingOrPublishingCredentials()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"devolutions-manifest-{Guid.NewGuid():N}.json");
        var builder = DistributedApplication.CreateBuilder(["--publisher", "manifest", "--output-path", outputPath]);
        Assert.True(builder.ExecutionContext.IsPublishMode);
        var server = AddServer(builder);
        var secret = server.AddSecret("password", VaultId, EntryId, "/payload/password");
        var consumer = builder.AddContainer("consumer", "unused-image").WithDevolutionsSecret("PASSWORD", secret);
        var parameter = Assert.IsType<ParameterResource>(secret.Resource.PublishParameter);
        Assert.True(parameter.Secret);
        Assert.Null(parameter.Default);
        Assert.Same(parameter, await secret.Resource.GetParameterAsync(TestContext.Current.CancellationToken));
        Assert.Equal("{password-value.value}", parameter.ValueExpression);
        await Assert.ThrowsAsync<InvalidOperationException>(() => secret.Resource.GetValueAsync(TestContext.Current.CancellationToken).AsTask());

        var environment = new EnvironmentCallbackContext(builder.ExecutionContext, consumer.Resource);
        foreach (var annotation in consumer.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await annotation.Callback(environment);
        }
        Assert.Same(parameter, environment.EnvironmentVariables["PASSWORD"]);

        try
        {
            await using var app = builder.Build();
            await app.RunAsync(TestContext.Current.CancellationToken);
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
            var resources = manifest.RootElement.GetProperty("resources");
            var publishedParameter = resources.GetProperty("password-value");
            Assert.Equal("parameter.v0", publishedParameter.GetProperty("type").GetString());
            Assert.Equal("{password-value.inputs.value}", publishedParameter.GetProperty("value").GetString());
            Assert.True(publishedParameter.GetProperty("inputs").GetProperty("value").GetProperty("secret").GetBoolean());
            Assert.Equal("{password-value.value}", resources.GetProperty("consumer").GetProperty("env").GetProperty("PASSWORD").GetString());
            Assert.False(resources.TryGetProperty("devolutions", out _));
            Assert.False(resources.TryGetProperty("app-key", out _));
            Assert.False(resources.TryGetProperty("app-secret", out _));
            var json = manifest.RootElement.GetRawText();
            Assert.DoesNotContain("test-key", json);
            Assert.DoesNotContain("test-app-secret", json);
            Assert.DoesNotContain("default", json);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Theory]
    [InlineData("http://example.test")]
    [InlineData("https://user:password@example.test")]
    [InlineData("https://example.test?token=secret")]
    [InlineData("https://example.test#fragment")]
    [InlineData("relative", UriKind.Relative)]
    public void InvalidServerUrisAreRejected(string url, UriKind kind = UriKind.Absolute)
    {
        var builder = DistributedApplication.CreateBuilder();
        Assert.Throws<ArgumentException>(() => builder.AddDevolutionsServer(
            "server", new Uri(url, kind), builder.AddParameter("key", secret: true), builder.AddParameter("secret", secret: true)));
    }

    [Fact]
    public void ApiCredentialsMustBeSecretAndIdsMustBePresent()
    {
        var builder = DistributedApplication.CreateBuilder();
        Assert.Throws<ArgumentException>(() => builder.AddDevolutionsServer(
            "bad-server", new Uri("https://example.test"), builder.AddParameter("key"), builder.AddParameter("secret", secret: true)));
        var server = AddServer(builder);
        Assert.Throws<ArgumentException>(() => server.AddSecret("bad-secret", Guid.Empty, EntryId, "/password"));
        Assert.Throws<ArgumentException>(() => server.AddSecret("bad-secret", VaultId, Guid.Empty, "/password"));
    }

    [Theory]
    [InlineData("/payload/password", """{"payload":{"password":"value"}}""")]
    [InlineData("/a~1b/~0key", """{"a/b":{"~key":"value"}}""")]
    [InlineData("/items/1/password", """{"items":[{},{"password":"value"}]}""")]
    [InlineData("/", """{"":"value"}""")]
    public void JsonPointerSelectsExactStringFields(string pointer, string json)
    {
        JsonSecretSelector.Validate(pointer);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("value", JsonSecretSelector.Read(document.RootElement, pointer));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("/bad~2escape")]
    [InlineData("/bad~")]
    [InlineData("")]
    public void InvalidJsonPointersAreRejected(string pointer) =>
        Assert.ThrowsAny<ArgumentException>(() => JsonSecretSelector.Validate(pointer));

    [Theory]
    [InlineData("""{"password":null}""", "/password")]
    [InlineData("""{"password":""}""", "/password")]
    [InlineData("""{"password":123}""", "/password")]
    [InlineData("""{"Password":"value"}""", "/password")]
    [InlineData("""{"items":["value"]}""", "/items/01")]
    [InlineData("""{"items":["value"]}""", "/items/-1")]
    [InlineData("""{"items":["value"]}""", "/items/1")]
    public void MissingEmptyOrNonStringFieldsFail(string json, string pointer)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<InvalidOperationException>(() => JsonSecretSelector.Read(document.RootElement, pointer));
    }

    internal static IResourceBuilder<DevolutionsServerResource> AddServer(IDistributedApplicationBuilder builder) =>
        builder.AddDevolutionsServer("devolutions", new Uri("https://example.test/dps"),
            builder.AddParameter("app-key", () => "test-key", secret: true).ExcludeFromManifest(),
            builder.AddParameter("app-secret", () => "test-app-secret", secret: true).ExcludeFromManifest());

    private static Task<IExecutionConfigurationResult> ResolveEnvironmentAsync(
        IResource resource, DistributedApplicationExecutionContext context) =>
        ExecutionConfigurationBuilder.Create(resource).WithEnvironmentVariablesConfig().WithArgumentsConfig()
            .BuildAsync(context, NullLogger.Instance, TestContext.Current.CancellationToken);
}

internal sealed class TestHttpHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        callback(request, cancellationToken);

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}
