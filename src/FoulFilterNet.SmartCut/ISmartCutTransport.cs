namespace FoulFilterNet.SmartCut;

/// <summary>
/// One LLM endpoint, reduced to the only thing Smart Cut asks of it: hand over a
/// prompt, get back whatever the model said.
/// </summary>
/// <remarks>
/// Implementations do not throw for transport trouble. An unreachable server, a
/// rate limit that outlasted the retries, or a refusal all come back as one of
/// the <see cref="SmartCutResponses"/> sentinels, which
/// <see cref="SmartCutResponseParser"/> turns into "keep the original
/// timestamps". Smart Cut is an optional refinement; it must never be able to
/// fail a job.
/// </remarks>
public interface ISmartCutTransport
{
    /// <summary>Send one prompt. Returns the model's text, or a sentinel.</summary>
    Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken = default);
}
