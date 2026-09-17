namespace FoulFilterNet.Jobs.Tests;

/// <summary>
/// Holds a stubbed pipeline call open so a test can observe the queue while a
/// job is genuinely mid-flight, without sleeping.
/// </summary>
internal sealed class Gate
{
    private readonly TaskCompletionSource _entered = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly TaskCompletionSource _released = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    /// <summary>Completes once the gated call has been reached.</summary>
    public Task Entered => _entered.Task;

    /// <summary>Lets the gated call proceed.</summary>
    public void Open() => _released.TrySetResult();

    /// <summary>Called from inside the stub to announce arrival without blocking.</summary>
    public void Reached() => _entered.TrySetResult();

    /// <summary>Called from inside the stub: announce arrival, then block.</summary>
    public async Task PassAsync()
    {
        _entered.TrySetResult();
        await _released.Task;
    }
}
