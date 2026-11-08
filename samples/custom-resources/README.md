# Writing custom resources for Aspire hosting integrations

This sample demonstrates how to write custom resources for Aspire hosting integrations. This is useful when you want to integrate something into the Aspire development experience as a resource that isn't an executable or container. Custom resources can particpate in the Aspire development experience, including the dashboard, and can be used to integrate with other tools or services.

Custom resources are defined using C# and generally consist of a class that implements the `IResource` interface and some extension methods to enable adding them to an `IDistributedApplicationBuilder`. Custom resources can publish and respond to events to give them "life" and allow them to interact with the rest of the Aspire application.

In this sample, we define a `TalkingClock` custom resource that spawns child `ClockHand` resources that tick on and off every second. We also define a `TestResource` custom resource that simply cycles through a set of states.

The sample also includes a `ScalarResource` derived from `ContainerResource`. It runs the [Scalar API Reference Docker image](https://scalar.com/products/api-references/integrations/docker) and automatically discovers OpenAPI document metadata across the application.

## Scalar OpenAPI resource

```csharp
var scalar = builder.AddScalar();

var api = builder.AddProject<Projects.CustomResources_ApiService>("api", launchProfileName: null)
    .WithHttpEndpoint()
    .WithOpenApi(title: "Sample API");
```

`WithOpenApi` attaches an `OpenApiAnnotation` containing an endpoint reference, document path, and display title. Aspire does not infer OpenAPI routes from an API project's `MapOpenApi` call, so declare each document on its hosting resource. This works with any resource implementing `IResourceWithEndpoints`, including projects, executables, and containers. Resources without this annotation are ignored.

The defaults are endpoint `http`, path `/openapi/v1.json`, and the resource name as the title. For other routes or multiple documents, call `WithOpenApi` for each:

```csharp
api.WithOpenApi("/swagger/v2/swagger.json", endpointName: "http", title: "Sample API v2");
```

Only register routes your API actually serves. Paths must begin with a single `/`; the selected endpoint must exist and use HTTP or HTTPS.

`AddScalar` scans **all** application resources in a deferred environment callback, including resources and metadata added after `AddScalar`. It waits for endpoint allocation, then serializes the discovered documents into Scalar's `API_REFERENCE_CONFIG` environment variable as `{"sources":[{"title":"...","url":"..."}]}`. Missing documents or invalid endpoint metadata produce explicit errors.

Scalar's JavaScript fetches documents in the **browser**, not in the container. URLs therefore use the host's allocated endpoints, not container DNS names or `host.docker.internal`. APIs must allow the Scalar UI's origin through CORS; the sample passes `scalar.GetEndpoint("http")` as `Scalar__Origin` and configures a matching CORS policy for document fetches and "Try it" requests. The sample also uses that same endpoint URL for Scalar's dashboard link so Aspire's hostname aliases do not introduce a different browser origin. If you serve Scalar under another hostname, allow that origin in your API's CORS policy too. HTTPS endpoints additionally require a browser-trusted certificate.

The container exposes HTTP on target port 8080 and checks `/health`. Set a host port with `builder.AddScalar(port: 8088)`; otherwise Aspire allocates it. Repeated calls return the same resource (the first name and port win). The image is pinned to `scalarapi/api-reference:0.6.22`; override it with `WithImageTag` if needed.

Following the [admin/tool container pattern](https://sliekens.dev/lessons/aspire/hosting-integration-authoring/0013-admin-and-tool-container.html), Scalar is development-only: it is excluded from the manifest and is not added to the application model in publish mode. APIs never wait for Scalar. The sample serves OpenAPI documents only in Development and explicitly selects that environment for local Aspire runs without a launch profile.

Read more about the [Aspire resource model here](https://gist.github.com/davidfowl/b408af870d4b5b54a28bf18bffa127e1).

## Prerequisites

- [Aspire development environment](https://aspire.dev/get-started/prerequisites/)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A container runtime, such as Docker Desktop, for Scalar.

## Running the app

If using the Aspire CLI, run `aspire run` from this directory.

If using VS Code, open this directory as a workspace and launch the `CustomResources.AppHost` project using either the Aspire or C# debuggers.

If using Visual Studio, open the solution file `CustomResources.slnx` and launch/debug the `CustomResources.AppHost` project.

If using the .NET CLI, run `dotnet run` from the `CustomResources.AppHost` directory.

Open the `scalar` HTTP endpoint from the Aspire dashboard and select **Sample API** to view its OpenAPI document and try `GET /hello`.

## Tests

Run the focused resource-model tests without a container runtime:

```shell
dotnet test CustomResources.Tests/CustomResources.Tests.csproj
```
