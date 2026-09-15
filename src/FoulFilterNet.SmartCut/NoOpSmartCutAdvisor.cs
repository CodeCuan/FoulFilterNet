using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;

namespace FoulFilterNet.SmartCut;

/// <summary>
/// What the pipeline gets when Smart Cut is off, or on but unconfigured. Every
/// hit is left exactly where the matcher put it.
/// </summary>
/// <remarks>
/// This exists so the flag never becomes a conditional in the pipeline: the
/// orchestrator always has an advisor, asks it for every hit, and the disabled
/// case costs one virtual call. Note what it does <em>not</em> do - it never
/// rejects a hit. A disabled refinement pass must not be able to reduce what
/// gets censored.
/// </remarks>
public sealed class NoOpSmartCutAdvisor : ISmartCutAdvisor
{
    /// <summary>The one instance; it has no state.</summary>
    public static NoOpSmartCutAdvisor Instance { get; } = new();

    public bool IsEnabled => false;

    public Task<SmartCutDecision> RefineAsync(
        IReadOnlyList<Word> contextWindow,
        string phrase,
        int centerIndex,
        bool allowWidening,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(SmartCutDecision.KeepOriginal);
}
