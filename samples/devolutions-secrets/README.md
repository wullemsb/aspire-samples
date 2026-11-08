# Devolutions Server secret provider

A custom C# Aspire hosting integration that reads credential entries from an
**existing, centrally hosted Devolutions Server** and injects selected fields into
application resources. No local Devolutions container or server is created.

## Prerequisites and setup

- .NET 10 SDK and Aspire CLI 13.6 (see the [repository setup](../../README.md)).
- An HTTPS Devolutions Server accessible from the AppHost, with a trusted TLS
  certificate. Virtual-directory URLs such as `https://server.example/dps/` work.
- A Devolutions API application key and secret with least-privilege read access
  to the selected vault and credential entry.
- The vault GUID, entry GUID, and the **exact JSON Pointer** to the desired string
  field in your server's entry response.

The [public API documentation](https://docs.devolutions.net/server/web-interface/utilities/api-documentation)
specifies these requests:

1. `POST api/v1/login` with JSON `{ "appKey": "...", "appSecret": "..." }`.
2. `GET api/v1/vault/{vaultId}/entry/{entryId}` with a `tokenId` header.
3. `POST api/v1/logout` with the same header and an empty body.

It does **not** specify the credential-entry response schema. Find that schema in
your server's **Help & Tools -> API documentation**, then configure the JSON
Pointer accordingly. For example, `/payload/password` selects the password in
`{ "payload": { "password": "..." } }`; this is an illustrative response, not an
assumption about Devolutions. Property names are case-sensitive. Nested objects,
array indexes, and JSON Pointer escapes (`~0` for `~`, `~1` for `/`) are supported.

The documented API retrieves passwords only from **Credentials** entries.
Subtype entries such as **PasswordList** are not supported.

From this sample directory, configure the AppHost with local user secrets:

```powershell
dotnet user-secrets set "Devolutions:ServerUrl" "https://your-server.example/dps/" --project .\Devolutions.AppHost
dotnet user-secrets set "Devolutions:VaultId" "<vault-guid>" --project .\Devolutions.AppHost
dotnet user-secrets set "Devolutions:EntryId" "<credential-entry-guid>" --project .\Devolutions.AppHost
dotnet user-secrets set "Devolutions:PasswordJsonPointer" "<json-pointer-from-your-api-schema>" --project .\Devolutions.AppHost
dotnet user-secrets set "Parameters:devolutions-app-key" "<application-key>" --project .\Devolutions.AppHost
dotnet user-secrets set "Parameters:devolutions-app-secret" "<application-secret>" --project .\Devolutions.AppHost

aspire start --apphost .\Devolutions.AppHost\Devolutions.AppHost.csproj --non-interactive
aspire wait consumer --apphost .\Devolutions.AppHost\Devolutions.AppHost.csproj --non-interactive
```

User secrets are development-only, unencrypted local storage. Do not commit them
or copy actual credentials into scripts, documentation, command history, or CI
logs. For automated runs, inject configuration from your CI secret store:
`Devolutions__ServerUrl`, `Devolutions__VaultId`, `Devolutions__EntryId`,
`Devolutions__PasswordJsonPointer`, `Parameters__devolutions-app-key`, and
`Parameters__devolutions-app-secret`.

For parallel worktrees, add `--isolated` to `aspire start` and supply configuration
through environment variables. Isolation also isolates user secrets, so the
non-isolated user-secrets settings above will not be used.

Open the consumer endpoint shown in the dashboard. It returns only
`{ "secretConfigured": true }`, never the secret. The consumer refuses to start if
the injected password is missing.

## Use the integration in another AppHost

Reference [Devolutions.Hosting](./Devolutions.Hosting/Devolutions.Hosting.csproj)
from your AppHost with `IsAspireProjectResource="false"`, then configure it:

```csharp
using Devolutions.Hosting;

var devolutions = builder.AddDevolutionsServer(
    "devolutions",
    new Uri(builder.Configuration["Devolutions:ServerUrl"]!),
    builder.AddParameter("devolutions-app-key", secret: true).ExcludeFromManifest(),
    builder.AddParameter("devolutions-app-secret", secret: true).ExcludeFromManifest());

var password = devolutions.AddSecret(
    "backend-password", vaultId, entryId, passwordJsonPointer);
var username = devolutions.AddSecret(
    "backend-username", vaultId, entryId, usernameJsonPointer);

builder.AddProject<Projects.Backend>("backend")
    .WithDevolutionsSecret("ExternalService__Username", username)
    .WithDevolutionsSecret("ExternalService__Password", password);
```

`WithDevolutionsSecret` works with any `IResourceWithEnvironment` consumer,
including projects, containers, and executables. Multiple consumers can share
the same secret resource. For advanced composition in an async environment
callback, `await secret.Resource.GetParameterAsync(context.CancellationToken)`
returns a secret `ParameterResource` that can be included in a
`ReferenceExpression`. Do not resolve it yourself during model construction or
log its resolved value.

## Lifecycle and security

- The provider and selected secrets are plain resources implementing
  `IResourceWithoutLifetime`, visible in the dashboard with explicit reference
  relationships. No endpoints or synthetic readiness health checks are added.
- Model construction and provider initialization perform **no network requests**.
  The first consuming environment callback logs in, reads the entry, selects its
  field, and logs out. Unused secrets are not fetched.
- A secret `ParameterResource` carrier preserves Aspire's sensitive-value
  classification. It is created only after an asynchronous fetch succeeds, so
  its value callback cannot cache a failed API call or block on network access.
  The runtime carrier is not registered as a configuration parameter, so users
  are not prompted to enter the fetched secret.
- Only successful values are cached, **in memory**, per selected secret for the
  AppHost lifetime. Concurrent consumers share a fetch. Failed or cancelled
  resolutions can be attempted again. No secret files or persistent caches are
  written. Restart the AppHost to pick up rotated credentials; restarting only
  a consumer reuses its cached secret.
- API credentials are separate from application secrets and are never injected
  into consuming resources. The sample excludes them from publishing.
- TLS validation stays enabled; redirects are rejected to prevent sending
  credentials or tokens to another endpoint. Requests have a 30-second timeout
  and a 1 MiB response limit. HttpClient request logging is disabled for this
  named client; only sanitized operation/status diagnostics are logged.
- Invalid responses, HTTP failures, missing fields, null/empty/non-string fields,
  and network errors fail explicitly. Logs and exceptions never forward provider
  response bodies, tokens, or raw provider exception messages.
- Logout is attempted after a successful login even when fetching fails or is
  cancelled, with its own 10-second timeout. A logout failure is logged and fails
  an otherwise successful fetch; it does not replace an earlier fetch failure.
- Injected environment values are necessarily present in the consumer process
  and AppHost memory. Aspire masking does not protect them from a process
  debugger, privileged machine users, or consumer code that logs configuration.

## Run, publish, and deploy

**Run:** existing credential entries are read on demand; consuming resources get
resolved environment values without `WaitFor(devolutions)`.

**Publish:** Devolutions is not contacted. Each selection becomes a required,
secret Aspire parameter named `<secret-resource-name>-value`, and consumers
reference that parameter. No provider-side credential or fetched secret is
exported as a default or plaintext. For example, `backend-password` becomes
`backend-password-value`.

**Deploy:** provide those parameters using your deployment target's secret
mechanism. This integration does not provision vaults, create or update entries,
grant permissions, or configure deployed workloads to read Devolutions directly.
Deploy-time secret synchronization and runtime workload identity are out of scope.

The design follows the
[secret/credentials provider archetype](https://sliekens.dev/lessons/aspire/hosting-integration-authoring/0030-archetype-secret-provider.html).

## Validation

```powershell
dotnet build .\DevolutionsSecrets.slnx
dotnet test .\Devolutions.Hosting.Tests\Devolutions.Hosting.Tests.csproj
```

Tests use an in-memory HTTP handler, not a real server. They exercise the API
protocol, virtual-directory URLs, lazy initialization, concurrency and caching,
cancellation/logout, JSON selection, sanitized failures, sensitive-value
classification, and actual Aspire manifest generation. These tests are included
in the repository's CI test solution; the real-server AppHost is excluded from
the generic startup tests. Validate the configured
JSON Pointer and application permissions against your own server before use.
