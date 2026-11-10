using AspireShop.CatalogDb;
using AspireShop.CatalogService;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<CatalogDbContext>("catalogdb");

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

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
    app.MapScalarApiReference();
}
else
{
    app.UseExceptionHandler();
}

app.MapCatalogApi();
app.MapDefaultEndpoints();

app.Run();
