using Aspire.Hosting.ApplicationModel;

namespace Devolutions.Hosting;

public sealed class DevolutionsServerResource : Resource, IResourceWithoutLifetime
{
    internal TaskCompletionSource<IServiceProvider> RuntimeServices { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal DevolutionsServerResource(
        string name, Uri serverUri, ParameterResource appKey, ParameterResource appSecret) : base(name)
    {
        ServerUri = serverUri;
        AppKey = appKey;
        AppSecret = appSecret;
    }

    public Uri ServerUri { get; }
    public ParameterResource AppKey { get; }
    public ParameterResource AppSecret { get; }
}
