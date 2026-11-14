# Aspire Shop

![Screenshot of the product catalog in the Aspire Shop sample](./images/aspireshop-frontend-light.png#gh-light-mode-only)
![Screenshot of the product catalog in the Aspire Shop sample](./images/aspireshop-frontend-dark.png#gh-dark-mode-only)

Browse a product catalog served by the Catalog service and add items to a cart held by the Basket service. Built with [Aspire](https://aspire.dev).

![Screenshot of more products in the Aspire Shop catalog](./images/aspireshop-frontend-2-light.png#gh-light-mode-only)
![Screenshot of more products in the Aspire Shop catalog](./images/aspireshop-frontend-2-dark.png#gh-dark-mode-only)

![Screenshot of the shopping cart in the Aspire Shop sample](./images/aspireshop-cart-light.png#gh-light-mode-only)
![Screenshot of the shopping cart in the Aspire Shop sample](./images/aspireshop-cart-dark.png#gh-dark-mode-only)

The app consists of four .NET services:

- **AspireShop.Frontend**: This is an ASP.NET Core Blazor app that displays a paginated catalog of products and allows users to add products to a shopping cart.
- **AspireShop.CatalogService**: This is an HTTP API that provides access to the catalog of products stored in a PostgreSQL database.
- **AspireShop.CatalogDbManager**: This is an HTTP API that manages the initialization and updating of the catalog database.
- **AspireShop.BasketService**: This is a gRPC service that provides access to the shopping cart stored in Redis.

The app also includes a .NET class library project, **AspireShop.ServiceDefaults**, that contains the code-based defaults used by the .NET service projects.

## Prerequisites

- [Aspire development environment](https://aspire.dev/get-started/prerequisites/)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## Running the app

If using the Aspire CLI, run `aspire run` from this directory.

If using VS Code, open this directory as a workspace and launch the `AspireShop.AppHost` project using either the Aspire or C# debuggers.

If using Visual Studio, open the solution file `AspireShop.slnx` and launch/debug the `AspireShop.AppHost` project.

If using the .NET CLI, run `dotnet run` from the `AspireShop.AppHost` directory.

The Catalog service's **Retrieve products** resource command accepts a positive brand ID and returns the first page of matching products as JSON (up to eight items).

## Scalar endpoint discovery event

When Scalar prepares its configuration locally, it publishes a `ScalarEndpointsDiscoveredEvent`
containing the discovered OpenAPI titles and browser-facing document URLs. The
`ScalarEndpointLogger` implements `IDistributedApplicationEventingSubscriber` and is
registered by `AddScalar()` using `TryAddEventingSubscriber`. It lists those endpoints
in the Scalar resource's console logs in the Aspire dashboard. The event is published
each time the environment configuration is evaluated, including on restarts; it is
not published in publish mode.

## Exporting the catalog OpenAPI document

In Development, the Catalog service's OpenAPI document allows cross-origin GET requests so the separate Scalar UI can fetch it regardless of its assigned port. This CORS policy applies only to the OpenAPI endpoint, does not allow credentials, and does not enable cross-origin API calls from Scalar's "Try it" feature. It is not enabled in production.

When running locally, the healthy Catalog service exposes an **Export catalog OpenAPI** command in the dashboard. It invokes `curl` on the AppHost machine to download the development-only `/openapi/v1.json` document using the service's current HTTP endpoint.

Ensure `curl` 7.83 or newer is on the AppHost's `PATH` (Windows includes `curl.exe`). You can also invoke the command from this directory:

```shell
aspire resource catalogservice export-openapi
```

Each export creates a timestamped `catalog-openapi-*.json` file under `AspireShop.AppHost/artifacts/openapi/`, which is ignored by Git. The command output shows its absolute path. Repeated exports preserve earlier files. HTTP errors and download failures fail the command, and downloads time out after 30 seconds.

## Linting OpenAPI in a custom terminal

On Windows, the Catalog service's **Open Spectral terminal** command opens an interactive shell in the dashboard's terminal dock. This demonstrates creating your own terminal with `TerminalService`, rather than attaching `WithTerminal()` to the API's server process. [Spectral](https://github.com/stoplightio/spectral) is a one-shot JSON/YAML linter; the shell stays open so you can run it repeatedly with different options.

Install Node.js 22 or 24+ with npm, ensuring both are on the AppHost's `PATH`. Before starting the shop, install the pinned local CLI from this sample directory:

```powershell
cd .\AspireShop.AppHost\Spectral
npm.cmd ci
```

No global Spectral installation is needed, and opening the terminal never installs or downloads packages. If dependencies are missing, the command reports the setup step instead of opening a nonfunctional terminal.

Start the app in Development, wait for `catalogservice` to become healthy, then select **Open Spectral terminal** from its resource commands. In the docked `cmd.exe` shell, run:

```batch
npm run lint:catalog
npm run lint:catalog -- --fail-severity warn
npm run --silent lint:catalog -- --format json
echo %ERRORLEVEL%
```

The command sets `CATALOG_OPENAPI_URL` to the service's current HTTP `/openapi/v1.json` URL and opens the shell in [AspireShop.AppHost/Spectral](./AspireShop.AppHost/Spectral). HTTP is intentional for local linting: Node.js does not need to trust the ASP.NET Core development HTTPS certificate. The document is only available in Development. Reopen the terminal if the service's endpoint changes.

The local [.spectral.yaml](./AspireShop.AppHost/Spectral/.spectral.yaml) extends Spectral's standard OpenAPI rules. Existing style-guide findings are expected; the sample does not change the API or suppress rules to make linting pass. Edit the ruleset and rerun to explore your own API guidelines. By default, errors cause a nonzero exit code; `--fail-severity warn` also fails on warnings. JSON mode emits a diagnostic array, and `--silent` removes npm's script banner. Check `%ERRORLEVEL%` immediately after the command whose result you want.

Lint failures do not close the shell, and successfully opening the terminal does not mean the API passed linting. Each invocation opens a separate session. Type `exit` when finished; closing its dock tab or shutting down the AppHost also disposes the terminal. Closing a detached viewer is not the same as disposing the session.

This command is local-run-only and Windows-only. It uses experimental Aspire terminal APIs (`ASPIRETERMINAL001`). The shell runs on the AppHost machine with the AppHost user's permissions, not inside a container or sandbox, so expose the dashboard only to trusted users.
