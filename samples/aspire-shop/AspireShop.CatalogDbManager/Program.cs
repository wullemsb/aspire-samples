using Microsoft.EntityFrameworkCore;
using AspireShop.CatalogDb;
using AspireShop.CatalogDbManager;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<CatalogDbContext>("catalogdb", null,
    optionsBuilder => optionsBuilder.UseNpgsql(npgsqlBuilder =>
        npgsqlBuilder.MigrationsAssembly(typeof(Program).Assembly.GetName().Name)));

builder.Services.AddOpenApi();

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(CatalogDbInitializer.ActivitySourceName));

builder.Services.AddSingleton<CatalogDbInitializer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CatalogDbInitializer>());
builder.Services.AddHealthChecks()
    .AddCheck<CatalogDbInitializerHealthCheck>("DbInitializer", null);

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(options => options.AddPolicy("OpenApi", policy =>
        policy.AllowAnyOrigin().WithMethods("GET","POST")));
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseCors();
    app.MapOpenApi().RequireCors("OpenApi");

    app.MapPost("/reset-db", async (CatalogDbContext dbContext, CatalogDbInitializer dbInitializer, CancellationToken cancellationToken) =>
    {
        // Delete and recreate the database. This is useful for development scenarios to reset the database to its initial state.
        await dbContext.Database.EnsureDeletedAsync(cancellationToken);
        await dbInitializer.InitializeDatabaseAsync(dbContext, cancellationToken);
    }).RequireCors("OpenApi");
}

app.MapDefaultEndpoints();

await app.RunAsync();
