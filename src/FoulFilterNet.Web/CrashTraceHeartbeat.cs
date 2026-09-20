using FoulFilterNet.Media;
using FoulFilterNet.Transcription;

namespace FoulFilterNet.Web;

/// <summary>
/// While <see cref="CrashTrace"/> is on, a line every few seconds saying what
/// the process and the card were holding.
/// </summary>
/// <remarks>
/// <para>
/// Two jobs. It is a heartbeat, so the gap between the last line and the crash
/// says whether the process died mid-step or sat still first. And it carries
/// the card's memory, because a native fast-fail out of whisper.cpp is
/// consistent with a CUDA allocation failing, which nothing managed would ever
/// see as an exception.
/// </para>
/// <para>
/// The card is read with <c>nvidia-smi</c>, which is a process launch every
/// tick - acceptable only because this runs when a crash is being chased, and
/// never otherwise. If it is not on the PATH, the memory is left out and the
/// heartbeat carries on.
/// </para>
/// </remarks>
public sealed class CrashTraceHeartbeat(IProcessRunner processes) : BackgroundService
{
    /// <summary>Between heartbeats: often enough to time a crash, rare enough to read.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    /// <summary>A card that does not answer this quickly is not worth waiting for.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static readonly string[] Query =
    [
        "--query-gpu=memory.used,memory.total,utilization.gpu,temperature.gpu",
        "--format=csv,noheader,nounits",
    ];

    private bool _card = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!CrashTrace.IsEnabled)
        {
            return;
        }

        using var ticks = new PeriodicTimer(Interval);

        while (await ticks.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var gpu = await ReadCardAsync(stoppingToken).ConfigureAwait(false);

            CrashTrace.Write(
                "heartbeat",
                $"managed={GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024)}MB "
                    + $"working={Environment.WorkingSet / (1024 * 1024)}MB "
                    + $"threads={System.Diagnostics.Process.GetCurrentProcess().Threads.Count} "
                    + $"gen2={GC.CollectionCount(2)}{gpu}"
            );
        }
    }

    /// <summary>
    /// <c>used,total,utilisation,temperature</c> from the card, or nothing at
    /// all. A heartbeat must never be the thing that throws.
    /// </summary>
    private async Task<string> ReadCardAsync(CancellationToken stoppingToken)
    {
        if (!_card)
        {
            return string.Empty;
        }

        using var patience = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        patience.CancelAfter(Patience);

        try
        {
            var result = await processes
                .RunAsync("nvidia-smi", Query, patience.Token)
                .ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                _card = false;
                return string.Empty;
            }

            var fields = result
                .StandardOutput.Split('\n')[0]
                .Split(',', StringSplitOptions.TrimEntries);

            return fields.Length < 4
                ? string.Empty
                : $" vram={fields[0]}/{fields[1]}MB gpu={fields[2]}% temp={fields[3]}C";
        }
        catch (ProcessNotStartedException)
        {
            // No nvidia-smi on this host. Ask once, then stop asking.
            _card = false;
            return string.Empty;
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            return string.Empty;
        }
    }
}
