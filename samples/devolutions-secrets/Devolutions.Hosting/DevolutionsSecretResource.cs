using Aspire.Hosting.ApplicationModel;

namespace Devolutions.Hosting;

public sealed class DevolutionsSecretResource : Resource, IResourceWithoutLifetime
{
    private readonly Func<CancellationToken, Task<string>> _resolve;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ParameterResource? _parameter;

    internal DevolutionsSecretResource(
        string name,
        DevolutionsServerResource server,
        Guid vaultId,
        Guid entryId,
        string jsonPointer,
        ParameterResource? publishParameter,
        Func<CancellationToken, Task<string>> resolve) : base(name)
    {
        Server = server;
        VaultId = vaultId;
        EntryId = entryId;
        JsonPointer = jsonPointer;
        PublishParameter = publishParameter;
        _resolve = resolve;
    }

    public DevolutionsServerResource Server { get; }
    public Guid VaultId { get; }
    public Guid EntryId { get; }
    public string JsonPointer { get; }
    public ParameterResource? PublishParameter { get; }

    public async ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
    {
        if (PublishParameter is not null)
        {
            throw new InvalidOperationException(
                $"Devolutions secret '{Name}' cannot be fetched during publishing. Supply its deployment secret parameter.");
        }

        var parameter = await GetParameterAsync(cancellationToken).ConfigureAwait(false);
        return await parameter.GetValueAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ParameterResource> GetParameterAsync(CancellationToken cancellationToken = default)
    {
        if (PublishParameter is not null)
        {
            return PublishParameter;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_parameter is null)
            {
                var value = await _resolve(cancellationToken).ConfigureAwait(false);
                // Publish the carrier only after success; its synchronous callback cannot cache a fetch failure.
                _parameter = new ParameterResource($"{Name}-value", _ => value, secret: true);
            }
            return _parameter;
        }
        finally
        {
            _gate.Release();
        }
    }

    public override string ToString() => $"{{{Name}-value.value}}";
}
