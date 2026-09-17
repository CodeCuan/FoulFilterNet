using Whisper.net;
using Whisper.net.LibraryLoader;

namespace FoulFilterNet.Transcription;

/// <summary>
/// Device selection, expressed the way Whisper.net expresses it: an order of
/// native runtimes to try.
/// </summary>
/// <remarks>
/// <para>
/// Whisper.net loads the first runtime in <c>RuntimeOptions.RuntimeLibraryOrder</c>
/// that the machine can actually load, so "CUDA if it is usable, else CPU" is
/// not a probe followed by a decision - it is the order itself. That also means
/// forcing CUDA is expressed by leaving no CPU entry to fall back to: a machine
/// without CUDA then fails to load a model, which is what a configuration error
/// should look like, rather than quietly transcribing an audiobook at CPU speed.
/// </para>
/// <para>
/// The order is process-global in Whisper.net and is only read while the first
/// factory loads, which is why <see cref="Apply"/> writes into a list the caller
/// owns rather than reaching for the global itself.
/// </para>
/// </remarks>
public static class WhisperRuntime
{
    /// <summary>The runtimes to try, in order, for a configured device.</summary>
    public static IReadOnlyList<RuntimeLibrary> LibraryOrder(TranscriptionDevice device) =>
        device switch
        {
            // Cuda12 follows Cuda because the shipped native library is built with
            // the CUDA 13 toolchain and a machine on 12.x drivers needs the other.
            TranscriptionDevice.Cuda => [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12],
            TranscriptionDevice.Cpu => [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            _ =>
            [
                RuntimeLibrary.Cuda,
                RuntimeLibrary.Cuda12,
                RuntimeLibrary.Cpu,
                RuntimeLibrary.CpuNoAvx,
            ],
        };

    /// <summary>
    /// Whether the model itself should be offered the GPU. Separate from the
    /// runtime order because the CUDA runtime can be loaded and still be asked
    /// to keep the weights on the host.
    /// </summary>
    public static bool UsesGpu(TranscriptionDevice device) => device is not TranscriptionDevice.Cpu;

    /// <summary>
    /// How the model itself is loaded: which device it may use, and whether
    /// whisper.cpp should run its DTW alignment pass.
    /// </summary>
    /// <param name="heads">
    /// The model's alignment heads, or null when whisper.cpp does not know them.
    /// DTW is switched on only with a matching preset - asking for the pass
    /// without heads to align against is the one way to configure it wrongly.
    /// </param>
    /// <remarks>
    /// DTW has to be decided here rather than per transcription: it is a
    /// property of the loaded model, not of a processor built over it.
    /// </remarks>
    public static WhisperFactoryOptions FactoryOptions(
        TranscriptionDevice device,
        WhisperAlignmentHeadsPreset? heads
    )
    {
        var options = WhisperFactoryOptions.Default;
        options.UseGpu = UsesGpu(device);
        options.UseDtwTimeStamps = heads is not null;
        options.HeadsPreset = heads ?? WhisperAlignmentHeadsPreset.None;
        return options;
    }

    /// <summary>
    /// Replace <paramref name="target"/> with the order for
    /// <paramref name="device"/>. In production the target is Whisper.net's own
    /// global list.
    /// </summary>
    public static void Apply(TranscriptionDevice device, IList<RuntimeLibrary> target)
    {
        ArgumentNullException.ThrowIfNull(target);

        target.Clear();
        foreach (var library in LibraryOrder(device))
        {
            target.Add(library);
        }
    }
}
