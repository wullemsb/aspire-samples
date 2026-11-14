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

The Frontend's **Reset Shop State** command uses `IInteractionService` to ask for confirmation before clearing Redis and recreating the catalog database. Select **Reset** to proceed or **Cancel** to leave the shop unchanged. If interactive confirmation is unavailable, the command fails without changing any data.

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

When running locally, the healthy Catalog service exposes an **Export catalog OpenAPI** command in the dashboard. It uses Aspire's `IInteractionService` to ask for a `.json` filename and destination folder, then invokes `curl` on the AppHost machine to download the development-only `/openapi/v1.json` document using the service's current HTTP endpoint.

Ensure `curl` 7.83 or newer is on the AppHost's `PATH` (Windows includes `curl.exe`). The command requires an interactive client; clients without interaction support receive an explicit error. From a CLI client with interaction support, invoke it from this directory:

```shell
aspire resource catalogservice export-openapi
```

The prompt defaults to a timestamped `catalog-openapi-*.json` filename under `AspireShop.AppHost/artifacts/openapi/`, which is ignored by Git. You can change both values. Relative folders are resolved from the AppHost directory, and missing folders are created only after submitting the prompt. Paths refer to the AppHost machine, not the browser's machine. Existing destination files are rejected. Canceling the prompt does not start a download or create a folder. The command output shows the absolute destination path. HTTP errors and download failures fail the command, and downloads time out after 30 seconds.
