var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrEmpty(builder.Configuration["ExternalService:Password"]))
{
    throw new InvalidOperationException("The required ExternalService:Password secret was not injected.");
}

var app = builder.Build();
app.MapGet("/", () => Results.Ok(new { SecretConfigured = true }));
app.Run();
