var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (builder.Configuration["Scalar:Origin"] is { } origin)
        {
            policy.WithOrigins(origin).AllowAnyHeader().AllowAnyMethod();
        }
    });
});

var app = builder.Build();

app.UseCors();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/hello", () => new { Message = "Hello from a custom Aspire resource sample!" });

app.Run();
