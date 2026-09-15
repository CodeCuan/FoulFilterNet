using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;
using Microsoft.Extensions.Logging;

namespace FoulFilterNet.SmartCut;

/// <summary>
/// Smart Cut end to end: build the prompt, ask a model, map the answer back onto
/// the context window.
/// </summary>
/// <remarks>
/// <see cref="RefineAsync"/> does not throw, for anything. That is not defensive
/// habit - it is the contract. Smart Cut is one optional refinement per hit on a
/// job that may have taken twenty minutes to transcribe, and no amount of LLM
/// flakiness is worth losing that. Everything that can go wrong resolves to
/// <see cref="SmartCutDecision.KeepOriginal"/>, which leaves the hit exactly
/// where the matcher put it.
/// </remarks>
public sealed class LlmSmartCutAdvisor : ISmartCutAdvisor
{
    private readonly ILogger<LlmSmartCutAdvisor> _logger;

    public LlmSmartCutAdvisor(ISmartCutTransport transport, ILogger<LlmSmartCutAdvisor> logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(logger);

        Transport = transport;
        _logger = logger;
    }

    /// <summary>The endpoint this advisor talks to. Exposed so composition is assertable.</summary>
    public ISmartCutTransport Transport { get; }

    public bool IsEnabled => true;

    public async Task<SmartCutDecision> RefineAsync(
        IReadOnlyList<Word> contextWindow,
        string phrase,
        int centerIndex,
        bool allowWidening,
        CancellationToken cancellationToken = default)
    {
        // An empty window is what SmartCutMapper.Map throws on, and there would be
        // nothing for the model to read anyway. Do not spend a round trip on it.
        if (contextWindow is null || contextWindow.Count == 0)
        {
            return SmartCutDecision.KeepOriginal;
        }

        try
        {
            var prompt = SmartCutPrompt.Build(contextWindow, phrase, allowWidening);
            var answer = await Transport.CompleteAsync(prompt, cancellationToken);

            return SmartCutResponseParser.Parse(answer, contextWindow, centerIndex, allowWidening);
        }
#pragma warning disable CA1031 // Deliberate: see the class remarks. Nothing here may reach the pipeline.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Smart Cut failed for {Phrase}; keeping the original timestamps.", phrase);

            return SmartCutDecision.KeepOriginal;
        }
    }
}
