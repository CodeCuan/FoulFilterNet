using System.Diagnostics.CodeAnalysis;

namespace FoulFilterNet.Sources;

/// <summary>
/// A Web Video: a provider and that provider's ID (<c>youtube</c>,
/// <c>dQw4w9WgXcQ</c>), rather than a file or a URL. A value, compared by
/// content, so two tabs on the same video resolve to one Watch Session.
/// </summary>
/// <remarks>
/// <para>
/// The extension sends the bare ID, never a URL, and this is the only way in:
/// the constructor is private and the properties are get-only, so a
/// <c>with</c> expression cannot smuggle an unvalidated ID past it. Only
/// <c>youtube</c> exists in V1. Its IDs are exactly
/// <see cref="YouTubeIdLength"/> ASCII characters from <c>[A-Za-z0-9_-]</c>,
/// compared case-sensitively (<c>abc</c> and <c>ABC</c> are different videos).
/// The provider is accepted in any case and stored lower case, since it is ours
/// to name, not YouTube's.
/// </para>
/// <para>
/// <b>An ID may legitimately start with <c>-</c>.</b> <c>-abcdefghij</c> is a
/// valid YouTube ID, and handed to yt-dlp as a bare argument it would be read
/// as an option. So an ID must never reach a command line on its own: the audio
/// source (W08) passes <see cref="WatchUrl"/>, which we build here and which
/// always starts with <c>https://</c>, as the last argument. Validation keeps
/// out everything that is not an ID - URLs, spaces, <c>=</c>, <c>%</c>, and
/// Unicode lookalikes - but it cannot make a leading dash safe; only building
/// the URL does that.
/// </para>
/// <para>
/// Network input goes through <see cref="TryCreate"/>, which never throws;
/// <see cref="Create"/> is for trusted code and tests.
/// </para>
/// </remarks>
public sealed record VideoRef
{
    /// <summary>The only provider in V1, in its canonical (lower-case) form.</summary>
    public const string YouTubeProvider = "youtube";

    /// <summary>The length of every YouTube video ID.</summary>
    public const int YouTubeIdLength = 11;

    /// <summary>Separates the provider from the ID in <see cref="Key"/>.</summary>
    private const char KeySeparator = '-';

    private const string YouTubeWatchUrlPrefix = "https://www.youtube.com/watch?v=";

    private VideoRef(string provider, string id)
    {
        Provider = provider;
        Id = id;
    }

    /// <summary>The provider, always lower case (<c>youtube</c>).</summary>
    public string Provider { get; }

    /// <summary>
    /// The provider's ID, exactly as given. May start with <c>-</c>; see the
    /// remarks before putting it anywhere near a command line.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// The canonical key, <c>youtube-&lt;id&gt;</c>, which is also the
    /// Transcript cache key. <see cref="TryParseKey"/> reads it back. An ID that
    /// starts with <c>-</c> gives a double dash (<c>youtube--abcdefghij</c>),
    /// which is unambiguous because the provider never contains one.
    /// </summary>
    public string Key => Provider + KeySeparator + Id;

    /// <summary>
    /// The canonical watch URL, <c>https://www.youtube.com/watch?v=&lt;id&gt;</c>.
    /// Built by us from a validated ID, so it is the one form that is safe to
    /// hand to yt-dlp. The ID needs no escaping: its alphabet is URL-safe.
    /// </summary>
    public string WatchUrl => YouTubeWatchUrlPrefix + Id;

    /// <summary>
    /// Validate a provider and ID from an untrusted source. Never throws.
    /// </summary>
    /// <param name="provider"><c>youtube</c>, in any case. Surrounding
    /// whitespace is not trimmed: the extension sends exactly this string, so
    /// anything else is not what it sent.</param>
    /// <param name="id">Exactly <see cref="YouTubeIdLength"/> characters from
    /// <c>[A-Za-z0-9_-]</c>.</param>
    /// <param name="video">The video, or null when either part is invalid.</param>
    public static bool TryCreate(
        string? provider,
        string? id,
        [NotNullWhen(true)] out VideoRef? video
    )
    {
        video =
            IsSupportedProvider(provider) && IsValidYouTubeId(id)
                ? new VideoRef(YouTubeProvider, id)
                : null;
        return video is not null;
    }

    /// <summary>
    /// Validate a provider and ID that are expected to be good.
    /// </summary>
    /// <exception cref="ArgumentException">Either part is invalid; the
    /// parameter name says which.</exception>
    public static VideoRef Create(string provider, string id)
    {
        if (!IsSupportedProvider(provider))
        {
            throw new ArgumentException(
                $"Unsupported video provider '{provider}'. Only '{YouTubeProvider}' is supported.",
                nameof(provider)
            );
        }

        if (!IsValidYouTubeId(id))
        {
            throw new ArgumentException(
                $"'{id}' is not a YouTube video ID: it must be exactly {YouTubeIdLength} characters from [A-Za-z0-9_-].",
                nameof(id)
            );
        }

        return new VideoRef(YouTubeProvider, id);
    }

    /// <summary>
    /// Read a canonical <see cref="Key"/> back. Never throws. Only the
    /// canonical form is accepted - the provider must already be lower case -
    /// so every key that parses is exactly the <see cref="Key"/> of its result.
    /// </summary>
    /// <remarks>
    /// The key is split at its <em>first</em> dash, since the provider has none
    /// and the ID may start with one.
    /// </remarks>
    public static bool TryParseKey(string? key, [NotNullWhen(true)] out VideoRef? video)
    {
        video = null;
        if (key is null)
        {
            return false;
        }

        var separator = key.IndexOf(KeySeparator, StringComparison.Ordinal);
        if (separator < 0)
        {
            return false;
        }

        var provider = key[..separator];
        if (!string.Equals(provider, YouTubeProvider, StringComparison.Ordinal))
        {
            return false;
        }

        return TryCreate(provider, key[(separator + 1)..], out video);
    }

    /// <summary>
    /// Read a canonical <see cref="Key"/> that is expected to be good.
    /// </summary>
    /// <exception cref="ArgumentException">It is not a canonical key.</exception>
    public static VideoRef ParseKey(string key) =>
        TryParseKey(key, out var video)
            ? video
            : throw new ArgumentException(
                $"'{key}' is not a video key of the form '{YouTubeProvider}{KeySeparator}<id>'.",
                nameof(key)
            );

    /// <summary>The <see cref="Key"/>, which is how a video appears in logs.</summary>
    public override string ToString() => Key;

    private static bool IsSupportedProvider(string? provider) =>
        string.Equals(provider, YouTubeProvider, StringComparison.OrdinalIgnoreCase);

    // Deliberately not char.IsLetterOrDigit: that accepts Cyrillic, fullwidth
    // and Arabic-Indic lookalikes. Only these 64 ASCII characters are an ID.
    private static bool IsValidYouTubeId([NotNullWhen(true)] string? id)
    {
        if (id is null || id.Length != YouTubeIdLength)
        {
            return false;
        }

        foreach (var c in id)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-')
            {
                return false;
            }
        }

        return true;
    }
}
