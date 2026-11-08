using Devolutions.Hosting;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

var server = builder.AddDevolutionsServer(
    "devolutions",
    new Uri(builder.Configuration["Devolutions:ServerUrl"]
        ?? throw new InvalidOperationException("Configure Devolutions:ServerUrl.")),
    builder.AddParameter("devolutions-app-key", secret: true).ExcludeFromManifest(),
    builder.AddParameter("devolutions-app-secret", secret: true).ExcludeFromManifest());

var password = server.AddSecret(
    "application-password",
    Guid.Parse(builder.Configuration["Devolutions:VaultId"]
        ?? throw new InvalidOperationException("Configure Devolutions:VaultId.")),
    Guid.Parse(builder.Configuration["Devolutions:EntryId"]
        ?? throw new InvalidOperationException("Configure Devolutions:EntryId.")),
    builder.Configuration["Devolutions:PasswordJsonPointer"]
        ?? throw new InvalidOperationException("Configure Devolutions:PasswordJsonPointer using your server's API schema."));

builder.AddProject<Projects.SecretConsumer>("consumer")
    .WithDevolutionsSecret("ExternalService__Password", password);

builder.Build().Run();
