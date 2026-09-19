using System.Diagnostics.CodeAnalysis;
using FoulFilterNet.Sources;

namespace FoulFilterNet.Web;

/// <summary>
/// <c>Watch:DevFileProvider</c>: the development-only <c>file</c> provider and
/// the end-to-end harness page (W16). <b>Off by default</b>, and only
/// configuration turns it on.
/// </summary>
public sealed class DevFileProviderOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Watch:DevFileProvider";

    /// <summary>Serve <c>file</c> videos and the harness. Never on unless configured.</summary>
    public bool Enabled { get; set; }

    /// <summary>The directory whose files are served, e.g. <c>tests/fixtures/media</c>. Required when enabled.</summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>
    /// The repository's <c>extension/</c> folder, whose <c>harness/</c> and
    /// <c>src/</c> the harness page is served from. Blank: found by walking up
    /// from the content root and the app's folder.
    /// </summary>
    public string ExtensionDirectory { get; set; } = string.Empty;
}

/// <summary>What the host made of <see cref="DevFileProviderOptions"/>, for logging and tests.</summary>
/// <param name="Enabled">The provider is on.</param>
/// <param name="Files">The served directory, when on.</param>
/// <param name="ExtensionDirectory">The extension folder the harness comes from, when on and found.</param>
public sealed record DevFileProviderStatus(
    bool Enabled,
    DevFileDirectory? Files,
    string? ExtensionDirectory
)
{
    /// <summary>Off.</summary>
    public static DevFileProviderStatus Off { get; } = new(false, null, null);
}

/// <summary>Reads and checks the dev file provider's configuration.</summary>
public static class DevFileProvider
{
    /// <summary>
    /// The service key the real (yt-dlp) source is registered under, so the
    /// dev source can wrap it and a test can replace just it.
    /// </summary>
    public const string RealSourceKey = "yt-dlp";

    /// <summary>
    /// The configuration, checked. Enabled with no directory, or with one that
    /// does not exist, stops the host: a dev switch that is on but broken
    /// should be loud, not quietly off.
    /// </summary>
    /// <exception cref="InvalidOperationException">Enabled but misconfigured.</exception>
    public static DevFileProviderStatus Read(
        IConfiguration configuration,
        params string[] searchFrom
    )
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options =
            configuration
                .GetSection(DevFileProviderOptions.SectionName)
                .Get<DevFileProviderOptions>()
            ?? new DevFileProviderOptions();
        if (!options.Enabled)
        {
            return DevFileProviderStatus.Off;
        }

        if (string.IsNullOrWhiteSpace(options.Directory))
        {
            throw new InvalidOperationException(
                $"{DevFileProviderOptions.SectionName}:Enabled is true but {DevFileProviderOptions.SectionName}:Directory is empty. Name the directory of media files to serve, or turn the dev file provider off."
            );
        }

        var files = new DevFileDirectory(options.Directory);
        if (!files.Exists)
        {
            throw new InvalidOperationException(
                $"{DevFileProviderOptions.SectionName}:Directory '{files.FullPath}' does not exist."
            );
        }

        string? extension;
        if (!string.IsNullOrWhiteSpace(options.ExtensionDirectory))
        {
            var configured = Path.GetFullPath(options.ExtensionDirectory);
            extension = HasHarness(configured) ? configured : null;
        }
        else
        {
            extension = searchFrom
                .Select(FindExtensionDirectory)
                .FirstOrDefault(found => found is not null);
        }

        return new DevFileProviderStatus(true, files, extension);
    }

    /// <summary>
    /// The nearest <c>extension/</c> folder with a <c>manifest.json</c> and a
    /// <c>harness/</c> folder, looking in <paramref name="start"/> and each
    /// folder above it.
    /// </summary>
    public static string? FindExtensionDirectory(string start)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(start);

        for (
            var folder = new DirectoryInfo(Path.GetFullPath(start));
            folder is not null;
            folder = folder.Parent
        )
        {
            var candidate = Path.Combine(folder.FullName, "extension");
            if (HasHarness(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The startup warning. Loud on purpose: this serves files from a directory
    /// to anything that can reach the port, and a harness page with it.
    /// </summary>
    [SuppressMessage(
        "Performance",
        "CA1848:Use the LoggerMessage delegates",
        Justification = "Logged once at startup."
    )]
    public static void WarnIfEnabled(DevFileProviderStatus status, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(logger);
        if (!status.Enabled)
        {
            return;
        }

        logger.LogWarning(
            "DEV FILE PROVIDER IS ON ({Switch}:Enabled=true). Watch Sessions accept provider 'file' for the files in {Directory}, which are served at /dev/media/, and the end-to-end harness is at /dev/harness/. This is for development only: turn it off before using the service for real.",
            DevFileProviderOptions.SectionName,
            status.Files!.FullPath
        );

        if (status.ExtensionDirectory is null)
        {
            logger.LogWarning(
                "The dev file provider could not find the extension folder (with its harness/), so /dev/harness/ is not served. Set {Switch}:ExtensionDirectory.",
                DevFileProviderOptions.SectionName
            );
        }
    }

    private static bool HasHarness(string extension) =>
        File.Exists(Path.Combine(extension, "manifest.json"))
        && Directory.Exists(Path.Combine(extension, "harness"));
}
