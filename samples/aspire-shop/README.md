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

## Exporting the catalog OpenAPI document

When running locally, the healthy Catalog service exposes an **Export catalog OpenAPI** command in the dashboard. It invokes `curl` on the AppHost machine to download the development-only `/openapi/v1.json` document using the service's current HTTP endpoint.

Ensure `curl` 7.83 or newer is on the AppHost's `PATH` (Windows includes `curl.exe`). You can also invoke the command from this directory:

```shell
aspire resource catalogservice export-openapi
```

Each export creates a timestamped `catalog-openapi-*.json` file under `AspireShop.AppHost/artifacts/openapi/`, which is ignored by Git. The command output shows its absolute path. Repeated exports preserve earlier files. HTTP errors and download failures fail the command, and downloads time out after 30 seconds.
