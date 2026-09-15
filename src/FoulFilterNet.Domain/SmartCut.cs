namespace FoulFilterNet.Domain;

/// <summary>What Smart Cut decided about one <see cref="Hit"/>.</summary>
public enum SmartCutOutcome
{
    /// <summary>Leave the hit's timestamps alone. Also the result of any failure.</summary>
    KeepOriginal,

    /// <summary>Drop the hit: a false positive, such as "hoe" the garden tool.</summary>
    Reject,

    /// <summary>Replace the hit's window with <see cref="SmartCutDecision.CutStart"/>/<see cref="SmartCutDecision.CutEnd"/>.</summary>
    Adjust,
}

/// <summary>
/// The advisor's verdict on a hit. Deliberately three-valued: the Python this
/// replaces returned <c>None</c>, <c>False</c> or a dict, and conflating the
/// first two would turn every LLM outage into mass under-censoring.
/// </summary>
public sealed record SmartCutDecision
{
    private SmartCutDecision(SmartCutOutcome outcome, double cutStart, double cutEnd)
    {
        Outcome = outcome;
        CutStart = cutStart;
        CutEnd = cutEnd;
    }

    public SmartCutOutcome Outcome { get; }

    public double CutStart { get; }

    public double CutEnd { get; }

    /// <summary>Keep the original timestamps - the safe result when anything goes wrong.</summary>
    public static SmartCutDecision KeepOriginal { get; } = new(SmartCutOutcome.KeepOriginal, 0, 0);

    /// <summary>Drop this hit entirely.</summary>
    public static SmartCutDecision Reject { get; } = new(SmartCutOutcome.Reject, 0, 0);

    /// <summary>Use a different window for this hit.</summary>
    public static SmartCutDecision Adjust(double cutStart, double cutEnd) =>
        new(SmartCutOutcome.Adjust, cutStart, cutEnd);
}

/// <summary>
/// Smart Cut configuration, bound from the <c>SmartCut</c> section of
/// appsettings. Disabled by default: it costs an LLM round trip per hit, it is
/// the only stage that can change <em>what</em> gets cut, and it needs either an
/// API key or a local server that may not be running.
/// </summary>
public sealed class SmartCutOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "SmartCut";

    /// <summary>Master switch. Off unless explicitly enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Which transport to use.</summary>
    public SmartCutMode Mode { get; set; } = SmartCutMode.Local;

    /// <summary>OpenAI-compatible chat completions endpoint.</summary>
    public string LocalUrl { get; set; } = "http://localhost:8080/v1/chat/completions";

    /// <summary>Model id to request; empty asks the server what it is running.</summary>
    public string LocalModel { get; set; } = string.Empty;

    /// <summary>Gemini model id.</summary>
    public string GoogleModel { get; set; } = "gemini-2.5-flash-lite";

    /// <summary>Words of context either side of the target word.</summary>
    public int ContextRadius { get; set; } = 11;
}

/// <summary>Which LLM transport Smart Cut talks to.</summary>
public enum SmartCutMode
{
    /// <summary>Any OpenAI-compatible server, such as llama.cpp or vLLM.</summary>
    Local,

    /// <summary>Google Gemini.</summary>
    Google,
}
