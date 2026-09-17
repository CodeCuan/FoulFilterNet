using Whisper.net;
using Whisper.net.LibraryLoader;

namespace FoulFilterNet.Transcription.Tests;

/// <summary>
/// <see cref="TranscriptionDevice.Auto"/> is the shipped default and means
/// "CUDA if it is usable, else CPU". Whisper.net decides that by trying each
/// native runtime in order, so the order <em>is</em> the policy.
/// </summary>
public sealed class WhenNoTranscriptionDeviceIsForced
{
    private readonly IReadOnlyList<RuntimeLibrary> _order = WhisperRuntime.LibraryOrder(
        TranscriptionDevice.Auto
    );

    public WhenNoTranscriptionDeviceIsForced() => _order.ShouldNotBeEmpty();

    [Fact]
    public void ReachesForTheGpuFirst() => _order[0].ShouldBe(RuntimeLibrary.Cuda);

    [Fact]
    public void TriesTheOlderCudaToolchainToo() => _order.ShouldContain(RuntimeLibrary.Cuda12);

    [Fact]
    public void FallsBackToTheCpu() => _order[^2].ShouldBe(RuntimeLibrary.Cpu);

    [Fact]
    public void FallsBackAgainForACpuWithoutAvx() => _order[^1].ShouldBe(RuntimeLibrary.CpuNoAvx);

    [Fact]
    public void AsksTheModelToUseTheGpu() =>
        WhisperRuntime.UsesGpu(TranscriptionDevice.Auto).ShouldBeTrue();
}

/// <summary>
/// Forcing CUDA is a statement about the machine, so a machine that cannot load
/// it is a configuration error rather than something to paper over: there is no
/// CPU entry left to fall back to.
/// </summary>
public sealed class WhenCudaIsForced
{
    private readonly IReadOnlyList<RuntimeLibrary> _order = WhisperRuntime.LibraryOrder(
        TranscriptionDevice.Cuda
    );

    public WhenCudaIsForced() => _order.ShouldNotBeEmpty();

    [Fact]
    public void NeverSilentlyFallsBackToTheCpu() => _order.ShouldNotContain(RuntimeLibrary.Cpu);

    [Fact]
    public void NotEvenToTheNoAvxCpuBuild() => _order.ShouldNotContain(RuntimeLibrary.CpuNoAvx);

    [Fact]
    public void OffersBothCudaToolchains() =>
        _order.ShouldBe([RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12]);

    [Fact]
    public void AsksTheModelToUseTheGpu() =>
        WhisperRuntime.UsesGpu(TranscriptionDevice.Cuda).ShouldBeTrue();
}

/// <summary>
/// The escape hatch from the Python's <c>ALIGN_DEVICE=cpu</c>: the card is
/// spoken for (a local Smart Cut LLM, ADR-0004's sibling concern) and
/// transcription must stay off it.
/// </summary>
public sealed class WhenTheCpuIsForced
{
    private readonly IReadOnlyList<RuntimeLibrary> _order = WhisperRuntime.LibraryOrder(
        TranscriptionDevice.Cpu
    );

    public WhenTheCpuIsForced() => _order.ShouldNotBeEmpty();

    [Fact]
    public void NeverTouchesTheGpuRuntime() => _order.ShouldNotContain(RuntimeLibrary.Cuda);

    [Fact]
    public void StartsWithThePlainCpuBuild() => _order[0].ShouldBe(RuntimeLibrary.Cpu);

    [Fact]
    public void KeepsTheNoAvxBuildForOlderProcessors() =>
        _order.ShouldContain(RuntimeLibrary.CpuNoAvx);

    [Fact]
    public void TellsTheModelNotToUseTheGpu() =>
        WhisperRuntime.UsesGpu(TranscriptionDevice.Cpu).ShouldBeFalse();
}

/// <summary>
/// Whisper.net's runtime order is process-global and only read while the first
/// factory loads, so applying it is a mutation of someone else's list. It is
/// written through a list the caller owns, which is what makes it assertable
/// here without touching the real runtime.
/// </summary>
public sealed class WhenApplyingTheDeviceChoiceToARuntimeOrder
{
    private readonly List<RuntimeLibrary> _target = [RuntimeLibrary.Vulkan, RuntimeLibrary.CoreML];

    public WhenApplyingTheDeviceChoiceToARuntimeOrder() =>
        WhisperRuntime.Apply(TranscriptionDevice.Cpu, _target);

    [Fact]
    public void ReplacesWhateverWasThere() =>
        _target.ShouldBe(WhisperRuntime.LibraryOrder(TranscriptionDevice.Cpu).ToList());

    [Fact]
    public void LeavesNothingOfTheDefaultOrderBehind() =>
        _target.ShouldNotContain(RuntimeLibrary.Vulkan);

    [Fact]
    public void RefusesAMissingTarget() =>
        Should.Throw<ArgumentNullException>(() =>
            WhisperRuntime.Apply(TranscriptionDevice.Auto, null!)
        );
}

/// <summary>
/// Loading a model whose alignment heads whisper.cpp knows: DTW timestamps are
/// what made word boundaries usable (ADR-0006), and they are switched on when
/// the model loads rather than when the processor is built.
/// </summary>
public sealed class WhenLoadingAModelWhoseAlignmentHeadsAreKnown
{
    private readonly WhisperFactoryOptions _options = WhisperRuntime.FactoryOptions(
        TranscriptionDevice.Auto,
        WhisperAlignmentHeadsPreset.LargeV3Turbo
    );

    [Fact]
    public void AsksForDtwWordTimestamps() => _options.UseDtwTimeStamps.ShouldBeTrue();

    [Fact]
    public void NamesTheHeadsThatMatchTheModel() =>
        _options.HeadsPreset.ShouldBe(WhisperAlignmentHeadsPreset.LargeV3Turbo);

    [Fact]
    public void OffersTheModelTheGpu() => _options.UseGpu.ShouldBeTrue();
}

/// <summary>
/// A model with no known heads, where asking for DTW anyway is the one way to
/// configure it wrongly: whisper.cpp would have no heads to align against.
/// </summary>
public sealed class WhenLoadingAModelWithNoKnownAlignmentHeads
{
    private readonly WhisperFactoryOptions _options = WhisperRuntime.FactoryOptions(
        TranscriptionDevice.Cpu,
        heads: null
    );

    [Fact]
    public void LeavesDtwOff() => _options.UseDtwTimeStamps.ShouldBeFalse();

    [Fact]
    public void NamesNoHeadsAtAll() =>
        _options.HeadsPreset.ShouldBe(WhisperAlignmentHeadsPreset.None);

    [Fact]
    public void KeepsTheModelOffTheGpu() => _options.UseGpu.ShouldBeFalse();
}
