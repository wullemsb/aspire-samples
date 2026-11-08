using CustomResources.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddTalkingClock("talking-clock");

builder.AddTestResource("test");

var scalar = builder.AddScalar();

var api = builder.AddProject<Projects.CustomResources_ApiService>("api", launchProfileName: null)
    .WithHttpEndpoint()
    .WithOpenApi(title: "Sample API");

if (builder.ExecutionContext.IsRunMode)
{
    scalar.WithUrlForEndpoint("http", url => url.Url = scalar.GetEndpoint("http").Url);
    api.WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
        .WithEnvironment("Scalar__Origin", scalar.GetEndpoint("http"));
}

builder.Build().Run();
