using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace FoulFilterNet.Web.Tests;

/// <summary>
/// The front end is vanilla ES6 with no build step, so it crossed from the
/// Python unchanged - which only holds if the URLs it was written against still
/// answer. The page asks for its assets under <c>/static</c> because that is
/// where the Python mounted them.
/// </summary>
public class WhenABrowserOpensTheApp : IDisposable
{
    private readonly FoulFilterApplication _app = new();
    private readonly HttpClient _client;
    private readonly HttpResponseMessage _response;
    private readonly string _html;

    public WhenABrowserOpensTheApp()
    {
        _client = _app.CreateClient();
        _response = _client
            .GetAsync(new Uri("/", UriKind.Relative), Api.Token)
            .GetAwaiter()
            .GetResult();
        _html = _response.Content.ReadAsStringAsync(Api.Token).GetAwaiter().GetResult();

        _response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void ServesThePageAsHtml() =>
        _response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");

    [Fact]
    public void ServesTheUiItselfRatherThanAnythingElse() =>
        _html.ShouldContain("<title>FoulFilter</title>");

    [Fact]
    public void KeepsTheStylesheetUrlThePageWasWrittenWith() =>
        _html.ShouldContain("/static/app.css");

    [Fact]
    public void KeepsTheScriptUrlThePageWasWrittenWith() => _html.ShouldContain("/static/app.js");

    public void Dispose()
    {
        _response.Dispose();
        _client.Dispose();
        _app.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The two files the page names in its own markup, fetched the way the browser
/// fetches them.
/// </summary>
public class WhenTheBrowserFetchesWhatThePageReferences : IDisposable
{
    private readonly FoulFilterApplication _app = new();
    private readonly HttpClient _client;
    private readonly HttpResponseMessage _stylesheet;
    private readonly HttpResponseMessage _script;
    private readonly HttpResponseMessage _offTheRoot;
    private readonly string _css;
    private readonly string _js;

    public WhenTheBrowserFetchesWhatThePageReferences()
    {
        _client = _app.CreateClient();

        _stylesheet = Get("/static/app.css");
        _script = Get("/static/app.js");
        _offTheRoot = Get("/app.js");

        _css = _stylesheet.Content.ReadAsStringAsync(Api.Token).GetAwaiter().GetResult();
        _js = _script.Content.ReadAsStringAsync(Api.Token).GetAwaiter().GetResult();

        _stylesheet.StatusCode.ShouldBe(HttpStatusCode.OK);
        _script.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void ServesTheStylesheetAsCss() =>
        _stylesheet.Content.Headers.ContentType?.MediaType.ShouldBe("text/css");

    [Fact]
    public void ServesTheScriptAsJavaScript() =>
        _script.Content.Headers.ContentType?.MediaType.ShouldBe("text/javascript");

    [Fact]
    public void ServesTheRealStylesheet() => _css.ShouldContain("#dropzone.drag");

    /// <summary>
    /// The script drives the API through the same URLs the endpoints are mapped
    /// on, so this doubles as a check that the port carried it across intact.
    /// </summary>
    [Fact]
    public void ServesTheRealScript() => _js.ShouldContain("new EventSource(\"/events\")");

    /// <summary>
    /// Mounted at <c>/static</c> and nowhere else, as the Python had it: serving
    /// the whole of wwwroot off the root would answer URLs the page never asks
    /// for.
    /// </summary>
    [Fact]
    public void DoesNotAlsoServeTheAssetsOffTheRoot() =>
        _offTheRoot.StatusCode.ShouldBe(HttpStatusCode.NotFound);

    private HttpResponseMessage Get(string path) =>
        _client.GetAsync(new Uri(path, UriKind.Relative), Api.Token).GetAwaiter().GetResult();

    public void Dispose()
    {
        _stylesheet.Dispose();
        _script.Dispose();
        _offTheRoot.Dispose();
        _client.Dispose();
        _app.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Finding 1, end to end. The Python advertised <c>ai_enhance: true</c> whenever
/// the environment looked configured and then never called Smart Cut, so the
/// badge announced a feature that never ran. Out of the box the flag is off, the
/// page ships the badge hidden, and the only thing that reveals it is the
/// service saying so.
/// </summary>
public class WhenSmartCutIsLeftAtItsDefault : IDisposable
{
    private readonly FoulFilterApplication _app = new();
    private readonly HttpClient _client;
    private readonly JsonElement _config;
    private readonly string _html;
    private readonly string _js;

    public WhenSmartCutIsLeftAtItsDefault()
    {
        _client = _app.CreateClient();

        _config = Api.GetAsync(_client, "/config").GetAwaiter().GetResult();
        _html = Read("/");
        _js = Read("/static/app.js");

        _html.ShouldNotBeNullOrWhiteSpace();
        _js.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TheServiceSaysSmartCutIsOff() =>
        _config.GetProperty("ai_enhance").GetBoolean().ShouldBeFalse();

    [Fact]
    public void ThePageShipsTheBadgeHidden() =>
        _html.ShouldContain("<span id=\"ai-badge\" class=\"badge\" hidden>Smart Cut active</span>");

    [Fact]
    public void NothingButTheServiceCanRevealIt() =>
        _js.ShouldContain("if (cfg.ai_enhance) $(\"#ai-badge\").hidden = false;");

    private string Read(string path)
    {
        using var response = _client
            .GetAsync(new Uri(path, UriKind.Relative), Api.Token)
            .GetAwaiter()
            .GetResult();
        return response.Content.ReadAsStringAsync(Api.Token).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Serving the UI in tests proves nothing about a deployed service: the test
/// host reads from the project directory, so the page would still be served if
/// the assets never reached the build output at all.
/// </summary>
public class WhenTheProjectIsBuilt
{
    private readonly string _wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");

    public WhenTheProjectIsBuilt() => Directory.Exists(_wwwroot).ShouldBeTrue();

    [Fact]
    public void ThePageIsCopiedAlongsideTheAssembly() =>
        File.Exists(Path.Combine(_wwwroot, "index.html")).ShouldBeTrue();

    [Fact]
    public void SoIsTheStylesheet() =>
        File.Exists(Path.Combine(_wwwroot, "app.css")).ShouldBeTrue();

    [Fact]
    public void SoIsTheScript() => File.Exists(Path.Combine(_wwwroot, "app.js")).ShouldBeTrue();
}
