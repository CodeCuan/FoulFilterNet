using FoulFilterNet.Domain.Abstractions;
using FoulFilterNet.Transcription;
using FoulFilterNet.Web.Contracts;
using Microsoft.Extensions.Options;

namespace FoulFilterNet.Web.Endpoints;

/// <summary>The one call the UI makes before it renders anything.</summary>
public static class ConfigEndpoints
{
    public static void MapConfigEndpoint(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(
            "/config",
            async (
                ISmartCutAdvisor smartCut,
                IOptions<TranscriptionOptions> transcription,
                IOptions<StorageOptions> storage,
                WebVideoAvailabilityCache webVideo,
                CancellationToken cancellationToken
            ) =>
                Results.Ok(
                    new ConfigView(
                        WireNames.CensorMethods,
                        // Finding 1: the Python reported this true whenever the
                        // environment looked configured, and then never called Smart
                        // Cut, so the UI's "Smart Cut active" badge was a lie. This is
                        // the advisor the pipeline will actually use - not the raw flag,
                        // which still answers true when the feature is enabled without
                        // an API key and the no-op advisor is what got resolved.
                        smartCut.IsEnabled,
                        transcription.Value.Model,
                        storage.Value.MaxUploadMegabytes,
                        // Cached: probing starts yt-dlp, which is slow to start,
                        // and this is the first call of every page load.
                        WebVideoView.From(await webVideo.GetAsync(cancellationToken))
                    )
                )
        );
    }
}
