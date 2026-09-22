namespace FoulFilterNet.Pipeline;

/// <summary>
/// The configuration keys both hosts read, spelled once. <c>appsettings.json</c>
/// uses them as sections, <c>Section__Key</c> environment variables use them with
/// the colon doubled into underscores, and <see cref="LegacyEnvironmentVariables"/>
/// maps the Python's variable names onto them.
/// </summary>
public static class ConfigurationKeys
{
    public const string DataDirectory = "Storage:DataDirectory";

    public const string TranscriptDirectory = "Storage:TranscriptDirectory";

    public const string BadWordsPath = "Storage:BadWordsPath";

    public const string MaxUploadMegabytes = "Storage:MaxUploadMegabytes";

    /// <summary>
    /// The Censor Method a terminal run uses when no flag names one. Web never
    /// reads it: the upload form always names a method, as it did in the Python.
    /// </summary>
    public const string CensorMethod = "Cli:CensorMethod";

    public const string Model = "Transcription:Model";

    public const string Language = "Transcription:Language";

    public const string UnloadAfterJob = "Transcription:UnloadAfterJob";

    /// <summary>Whether the Priority Word Pass runs; see <c>TranscriptionOptions.PriorityPass</c>.</summary>
    public const string PriorityPass = "Transcription:PriorityPass";

    /// <summary>The Priority Word List file; unset means <c>priority_words.txt</c> in the data directory.</summary>
    public const string PriorityWordsPath = "Transcription:PriorityWordsPath";

    public const string SmartCutEnabled = "SmartCut:Enabled";

    public const string SmartCutMode = "SmartCut:Mode";

    public const string LocalUrl = "SmartCut:LocalUrl";

    public const string LocalModel = "SmartCut:LocalModel";
}
