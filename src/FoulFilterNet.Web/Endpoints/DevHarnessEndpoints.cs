using FoulFilterNet.Sources;

namespace FoulFilterNet.Web.Endpoints;

/// <summary>
/// The end-to-end harness (W16), mapped only when the dev file provider is on:
/// a page that plays a file from the dev directory in a <c>&lt;video&gt;</c>
/// and runs the extension's real content-script modules against this service.
/// </summary>
/// <remarks>
/// <para>
/// Everything is same-origin on purpose. The media must be, or Web Audio would
/// treat the element as tainted and give silence; the modules are, so they can
/// be imported without an extension; and the harness reaches the Watch
/// endpoints by plain <c>fetch</c> in place of the service worker.
/// </para>
/// <para>
/// Every file is found by name in its directory's listing
/// (<see cref="DevFileDirectory"/>), never by combining a path from the URL,
/// and only these types are served: the media directory's files, the
/// harness's <c>.html</c>, <c>.js</c>, <c>.css</c> and <c>.json</c>, and the
/// extension's <c>src/*.js</c>. The extension files are read from the
/// repository as they are, never cached, so an edit shows on reload.
/// </para>
/// </remarks>
public static class DevHarnessEndpoints
{
    private static readonly Dictionary<string, string> HarnessTypes = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
    };

    private static readonly Dictionary<string, string> ModuleTypes = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        [".js"] = "text/javascript; charset=utf-8",
    };

    private static readonly Dictionary<string, string> MediaTypes = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".webm"] = "video/webm",
        [".mkv"] = "video/x-matroska",
        [".mp3"] = "audio/mpeg",
        [".m4a"] = "audio/mp4",
        [".m4b"] = "audio/mp4",
        [".aac"] = "audio/aac",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".opus"] = "audio/ogg",
        [".flac"] = "audio/flac",
        [".json"] = "application/json; charset=utf-8",
    };

    public static void MapDevHarness(this WebApplication app, DevFileProviderStatus status)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(status);
        if (!status.Enabled || status.Files is null)
        {
            return;
        }

        var media = status.Files;
        app.MapGet("/dev/media", () => Results.Ok(media.Names));
        app.MapGet(
            "/dev/media/{name}",
            (string name) =>
                media.TryResolve(name, out var path)
                    ? Results.File(
                        path,
                        MediaTypes.GetValueOrDefault(
                            Path.GetExtension(path),
                            "application/octet-stream"
                        ),
                        enableRangeProcessing: true
                    )
                    : Results.NotFound()
        );

        if (status.ExtensionDirectory is not { } extension)
        {
            return;
        }

        var harness = new DevFileDirectory(Path.Combine(extension, "harness"));
        var modules = new DevFileDirectory(Path.Combine(extension, "src"));

        // Routing ignores a trailing slash, so one endpoint answers both: the
        // folder is the page, and the bare name redirects to the folder so the
        // page's relative URLs (harness.js, ../src/...) resolve under /dev/.
        app.MapGet(
            "/dev/harness",
            (HttpContext context) =>
                context.Request.Path.Value?.EndsWith('/') == true
                    ? Serve(context, harness, "index.html", HarnessTypes)
                    : Results.Redirect("/dev/harness/")
        );
        app.MapGet(
            "/dev/harness/{name}",
            (HttpContext context, string name) => Serve(context, harness, name, HarnessTypes)
        );
        app.MapGet(
            "/dev/src/{name}",
            (HttpContext context, string name) => Serve(context, modules, name, ModuleTypes)
        );
    }

    private static IResult Serve(
        HttpContext context,
        DevFileDirectory directory,
        string name,
        Dictionary<string, string> types
    )
    {
        if (
            !types.TryGetValue(Path.GetExtension(name), out var contentType)
            || !directory.TryResolve(name, out var path)
        )
        {
            return Results.NotFound();
        }

        context.Response.Headers.CacheControl = "no-store";
        return Results.File(path, contentType);
    }
}
