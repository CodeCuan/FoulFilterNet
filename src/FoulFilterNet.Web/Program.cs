// Composition root. Endpoints arrive in T26; static UI in T29.
var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

/// <summary>Exposed so <c>WebApplicationFactory</c> can target this host in tests.</summary>
public partial class Program;
