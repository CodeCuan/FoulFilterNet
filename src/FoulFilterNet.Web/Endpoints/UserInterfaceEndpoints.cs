namespace FoulFilterNet.Web.Endpoints;

/// <summary>
/// The batch UI: one page and the two files it references.
/// </summary>
/// <remarks>
/// <para>
/// The front end is vanilla ES6 with no build step and no dependencies, so it
/// crossed from the Python unchanged - which is worth preserving, and only holds
/// while the URLs it was written against keep answering. It asks for
/// <c>/static/app.css</c> and <c>/static/app.js</c> because that is where
/// FastAPI mounted them, so the asset mount keeps that prefix rather than the
/// page being edited to match a new one.
/// </para>
/// <para>
/// Nothing is served off the root but the page itself. Serving the whole of
/// wwwroot there as well would answer URLs the UI never asks for, for no gain.
/// </para>
/// </remarks>
public static class UserInterfaceEndpoints
{
    public static void MapUserInterface(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseStaticFiles(new StaticFileOptions { RequestPath = "/static" });

        app.MapGet(
            "/",
            (IWebHostEnvironment environment) =>
                Results.File(
                    Path.Combine(environment.WebRootPath, "index.html"),
                    "text/html; charset=utf-8"
                )
        );
    }
}
