using System.Text.Json;

namespace FoulFilterNet.Web.Tests;

/// <summary>
/// Finding 1: the Python's <c>/config</c> reported <c>ai_enhance: true</c>
/// whenever the environment looked configured, and then never called Smart Cut,
/// so the UI's badge advertised a feature that never ran. The flag reported here
/// is the one the pipeline resolves its advisor from.
/// </summary>
public class WhenTheUiAsksWhatIsConfigured : IDisposable
{
    private readonly FoulFilterApplication _app = new();
    private readonly HttpClient _client;
    private readonly JsonElement _config;

    public WhenTheUiAsksWhatIsConfigured()
    {
        _client = _app.CreateClient();
        _config = Api.GetAsync(_client, "/config").GetAwaiter().GetResult();

        _config.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public void SaysSmartCutIsOffUnlessSomeoneTurnedItOn() =>
        _config.GetProperty("ai_enhance").GetBoolean().ShouldBeFalse();

    [Fact]
    public void OffersTheThreeCensorMethods() =>
        _config
            .GetProperty("censor_methods")
            .EnumerateArray()
            .Select(m => m.GetString())
            .ShouldBe(["silence", "bleep", "remove"]);

    [Fact]
    public void ReportsTheUploadCapTheUiPrints() =>
        _config.GetProperty("max_upload_mb").GetInt32().ShouldBe(4096);

    [Fact]
    public void NamesTheTranscriptionModel() =>
        _config.GetProperty("whisper_model").GetString().ShouldNotBeNullOrWhiteSpace();

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class WhenSmartCutHasBeenTurnedOn : IDisposable
{
    private readonly FoulFilterApplication _app = new();
    private readonly HttpClient _client;
    private readonly JsonElement _config;

    public WhenSmartCutHasBeenTurnedOn()
    {
        _client = _app.WithWebHostBuilder(builder => builder.UseSetting("SmartCut:Enabled", "true"))
            .CreateClient();
        _config = Api.GetAsync(_client, "/config").GetAwaiter().GetResult();
    }

    [Fact]
    public void TheBadgeIsAllowedToAppear() =>
        _config.GetProperty("ai_enhance").GetBoolean().ShouldBeTrue();

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
        GC.SuppressFinalize(this);
    }
}
