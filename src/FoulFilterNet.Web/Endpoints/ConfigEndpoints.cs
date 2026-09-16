using FoulFilterNet.Domain;
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

        app.MapGet("/config", (
            IOptions<SmartCutOptions> smartCut,
            IOptions<TranscriptionOptions> transcription,
            IOptions<StorageOptions> storage) =>
            Results.Ok(new ConfigView(
                WireNames.CensorMethods,

                // Finding 1: the Python reported this true whenever the
                // environment looked configured, and then never called Smart
                // Cut, so the UI's "Smart Cut active" badge was a lie. This is
                // the flag the pipeline actually resolves its advisor from, and
                // it is off unless someone turns it on.
                smartCut.Value.Enabled,
                transcription.Value.Model,
                storage.Value.MaxUploadMegabytes)));
    }
}
