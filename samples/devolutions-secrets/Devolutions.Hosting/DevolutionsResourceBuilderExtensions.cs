using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Devolutions.Hosting;

public static class DevolutionsResourceBuilderExtensions
{
    public static IResourceBuilder<DevolutionsServerResource> AddDevolutionsServer(
        this IDistributedApplicationBuilder builder,
        string name,
        Uri serverUri,
        IResourceBuilder<ParameterResource> appKey,
        IResourceBuilder<ParameterResource> appSecret)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(serverUri);
        ArgumentNullException.ThrowIfNull(appKey);
        ArgumentNullException.ThrowIfNull(appSecret);
        if (!serverUri.IsAbsoluteUri || serverUri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(serverUri.UserInfo) || !string.IsNullOrEmpty(serverUri.Query)
            || !string.IsNullOrEmpty(serverUri.Fragment))
        {
            throw new ArgumentException("The server URL must be an absolute HTTPS URL without credentials, query or fragment.", nameof(serverUri));
        }
        if (!appKey.Resource.Secret || !appSecret.Resource.Secret)
        {
            throw new ArgumentException("Both API credential parameters must be marked secret.");
        }

        builder.Services.AddHttpClient(DevolutionsApiClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.MaxResponseContentBufferSize = 1024 * 1024;
        })
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

        var normalizedUri = new Uri(serverUri.AbsoluteUri.TrimEnd('/') + "/");
        return builder.AddResource(new DevolutionsServerResource(name, normalizedUri, appKey.Resource, appSecret.Resource))
            .WithInitialState(new()
            {
                ResourceType = "Devolutions Server",
                State = "External",
                Properties = []
            })
            .WithRelationship(appKey.Resource, "Reference")
            .WithRelationship(appSecret.Resource, "Reference")
            .OnInitializeResource((resource, context, cancellationToken) =>
            {
                resource.RuntimeServices.TrySetResult(context.Services);
                return Task.CompletedTask;
            })
            .ExcludeFromManifest();
    }

    public static IResourceBuilder<DevolutionsSecretResource> AddSecret(
        this IResourceBuilder<DevolutionsServerResource> builder,
        string name, Guid vaultId, Guid entryId, string jsonPointer)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (vaultId == Guid.Empty || entryId == Guid.Empty)
        {
            throw new ArgumentException("Vault and entry IDs must be non-empty GUIDs.");
        }
        JsonSecretSelector.Validate(jsonPointer);

        var appBuilder = builder.ApplicationBuilder;
        var publishParameter = appBuilder.ExecutionContext.IsPublishMode
            ? appBuilder.AddParameter($"{name}-value", secret: true).Resource
            : null;

        var resource = new DevolutionsSecretResource(
            name, builder.Resource, vaultId, entryId, jsonPointer, publishParameter,
            async cancellationToken =>
            {
                var services = await builder.Resource.RuntimeServices.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                var factory = services.GetRequiredService<IHttpClientFactory>();
                using var client = factory.CreateClient(DevolutionsApiClient.HttpClientName);
                var logger = services.GetRequiredService<ResourceLoggerService>().GetLogger(builder.Resource);
                return await new DevolutionsApiClient(client, logger).FetchAsync(
                    builder.Resource, name, vaultId, entryId, jsonPointer, cancellationToken).ConfigureAwait(false);
            });

        return appBuilder.AddResource(resource)
            .WithInitialState(new()
            {
                ResourceType = "Devolutions Secret",
                State = "External",
                Properties = []
            })
            .WithRelationship(builder.Resource, "Reference")
            .ExcludeFromManifest();
    }

    public static IResourceBuilder<T> WithDevolutionsSecret<T>(
        this IResourceBuilder<T> builder, string environmentVariable,
        IResourceBuilder<DevolutionsSecretResource> secret) where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentVariable);
        ArgumentNullException.ThrowIfNull(secret);

        return builder.WithEnvironment(async context =>
            {
                context.EnvironmentVariables[environmentVariable] =
                    await secret.Resource.GetParameterAsync(context.CancellationToken).ConfigureAwait(false);
            })
            .WithRelationship(secret.Resource, "Reference");
    }
}
