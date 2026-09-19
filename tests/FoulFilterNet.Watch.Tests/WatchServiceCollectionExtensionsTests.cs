using FoulFilterNet.Domain.Abstractions;
using FoulFilterNet.Pipeline;
using FoulFilterNet.Sources;
using FoulFilterNet.Transcription;
using FoulFilterNet.Watch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FoulFilterNet.Watch.Tests;

/// <summary>A service collection with the adapters a host registers for Jobs, faked.</summary>
internal static class WatchHost
{
    public static ServiceCollection Services(Harness harness)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWebAudioSource>(harness.Source);
        services.AddSingleton<IAudioPreparer>(harness.Preparer);
        services.AddSingleton<IWhisperEngine>(harness.Engine);
        return services;
    }

    public static IConfiguration Configuration(Harness harness, string idleTimeout = "00:05:00") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Watch:IdleTimeout"] = idleTimeout,
                    ["Watch:ScratchDirectory"] = harness.ScratchRoot,
                    ["Watch:TranscriptDirectory"] = Path.Combine(harness.Root, "transcripts"),
                    ["Watch:BadWordsPath"] = Path.Combine(harness.Root, "bad_words.txt"),
                }
            )
            .Build();
}

public sealed class WhenWatchIsAddedFromConfiguration : IDisposable
{
    private readonly Harness _harness = new();
    private readonly ServiceProvider _provider;
    private readonly WatchSessionManager _manager;

    public WhenWatchIsAddedFromConfiguration()
    {
        var services = WatchHost.Services(_harness);
        services.AddWatch(WatchHost.Configuration(_harness));
        _provider = services.BuildServiceProvider();
        _manager = _provider.GetRequiredService<WatchSessionManager>();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _harness.Dispose();
    }

    [Fact]
    public void BindsTheWatchSection() =>
        _manager.Options.IdleTimeout.ShouldBe(TimeSpan.FromMinutes(5));

    [Fact]
    public void KeepsTheDefaultsItWasNotGiven() =>
        _manager.Options.CompletedRetention.ShouldBe(TimeSpan.FromMinutes(30));

    [Fact]
    public void KeepsTheDefaultHeadLength() =>
        _manager.Options.HeadSeconds.ShouldBe(WatchOptions.DefaultHeadSeconds);

    [Fact]
    public void MakesOneManager() =>
        _provider.GetRequiredService<WatchSessionManager>().ShouldBeSameAs(_manager);

    [Fact]
    public void ReadsTheBadWordsListFromItsFile() =>
        _provider
            .GetRequiredService<IBadWordsSource>()
            .ShouldBeOfType<FileBadWordsSource>()
            .Path.ShouldBe(Path.Combine(_harness.Root, "bad_words.txt"));

    [Fact]
    public void RunsTheSweeper() =>
        _provider.GetServices<IHostedService>().OfType<WatchSessionSweeper>().Count().ShouldBe(1);

    [Fact]
    public void UsesTheSystemClock() =>
        _provider.GetRequiredService<TimeProvider>().ShouldBeSameAs(TimeProvider.System);
}

public sealed class WhenWatchIsAddedTwice : IDisposable
{
    private readonly Harness _harness = new();
    private readonly ServiceProvider _provider;

    public WhenWatchIsAddedTwice()
    {
        var services = WatchHost.Services(_harness);
        services.AddWatch(WatchHost.Configuration(_harness));
        services.AddWatch(WatchHost.Configuration(_harness));
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _harness.Dispose();
    }

    [Fact]
    public void StillSweepsOnce() =>
        _provider.GetServices<IHostedService>().OfType<WatchSessionSweeper>().Count().ShouldBe(1);

    [Fact]
    public void StillRegistersOneManager() =>
        _provider.GetServices<WatchSessionManager>().Count().ShouldBe(1);
}

public sealed class WhenWatchIsAddedWithTheHostsOwnPieces : IDisposable
{
    private readonly Harness _harness = new();
    private readonly ServiceProvider _provider;
    private readonly List<string> _storeDirectories = [];
    private readonly WatchSessionManager _manager;

    public WhenWatchIsAddedWithTheHostsOwnPieces()
    {
        var services = WatchHost.Services(_harness);
        services.AddSingleton<TimeProvider>(_harness.Time);
        services.AddSingleton<IBadWordsSource>(_harness.BadWords);
        services.AddSingleton<TranscriptStoreFactory>(directory =>
        {
            _storeDirectories.Add(directory);
            return _harness.Store;
        });
        services.AddWatch(options =>
        {
            options.ScratchDirectory = _harness.ScratchRoot;
            options.TranscriptDirectory = Path.Combine(_harness.Root, "cache");
            options.FailedRetention = TimeSpan.FromSeconds(10);
        });
        _provider = services.BuildServiceProvider();
        _manager = _provider.GetRequiredService<WatchSessionManager>();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _harness.Dispose();
    }

    [Fact]
    public void AppliesTheOptionsGivenInCode() =>
        _manager.Options.FailedRetention.ShouldBe(TimeSpan.FromSeconds(10));

    [Fact]
    public void BuildsTheCacheWithTheHostsFactory() =>
        _storeDirectories.ShouldBe([Path.Combine(_harness.Root, "cache")]);

    [Fact]
    public void KeepsTheHostsBadWordsSource() =>
        _provider.GetRequiredService<IBadWordsSource>().ShouldBeSameAs(_harness.BadWords);

    [Fact]
    public void KeepsTheHostsClock() =>
        _provider.GetRequiredService<TimeProvider>().ShouldBeSameAs(_harness.Time);

    [Fact]
    public void ServesACachedVideoThroughTheHostsStore()
    {
        _harness.Store.Cached = Harness.CachedTranscript(Harness.Video.Key);
        _manager.Heartbeat(Harness.Video, 0.0);

        Waits.Until(() => _manager.Find(Harness.Video)?.State == WatchState.Complete);
        _harness.Store.Finds.ShouldBe(1);
    }
}

public sealed class WhenWatchIsAddedWithNothing
{
    [Fact]
    public void RefusesNullServices() =>
        Should.Throw<ArgumentNullException>(() => WatchServiceCollectionExtensions.AddWatch(null!));

    [Fact]
    public void RefusesNullConfiguration() =>
        Should.Throw<ArgumentNullException>(() =>
            new ServiceCollection().AddWatch((IConfiguration)null!)
        );

    [Fact]
    public void RefusesANullConfigureAction() =>
        Should.Throw<ArgumentNullException>(() =>
            new ServiceCollection().AddWatch((Action<WatchOptions>)null!)
        );
}

public sealed class WhenTheSweeperRunsPastTheIdleTimeout : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSessionManager _manager;
    private readonly WatchSessionSweeper _sweeper;

    public WhenTheSweeperRunsPastTheIdleTimeout()
    {
        var fetch = _harness.Source.Hold = new Gate();
        var options = _harness.Options();
        _manager = _harness.Manager(options);
        _sweeper = new WatchSessionSweeper(
            _manager,
            Options.Create(options),
            _harness.Time,
            NullLogger<WatchSessionSweeper>.Instance
        );

        _sweeper.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        _manager.Heartbeat(Harness.Video, 0.0);
        fetch.WaitUntilEntered();

        // Since .NET 10 a BackgroundService runs ExecuteAsync on the thread
        // pool, so its timer may not exist yet when StartAsync returns. Keep
        // the clock moving past the timeout until a sweep has seen it.
        _harness.Time.Advance(options.IdleTimeout);
        Waits.Until(() =>
        {
            _harness.Time.Advance(TimeSpan.FromSeconds(1));
            return _manager.Count == 0;
        });
    }

    public void Dispose()
    {
        _sweeper.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _sweeper.Dispose();
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void DropsTheIdleSession() => _manager.Find(Harness.Video).ShouldBeNull();

    [Fact]
    public void StopsItsDownload() => Waits.Until(() => _harness.ScratchIsEmpty).ShouldBeTrue();

    [Fact]
    public void NeverGetsAsFarAsTheEngine() => _harness.Engine.Opened.ShouldBeEmpty();
}

public sealed class WhenTheSweeperTicksBeforeAnythingIsIdle : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSessionManager _manager;
    private readonly WatchSessionSweeper _sweeper;

    public WhenTheSweeperTicksBeforeAnythingIsIdle()
    {
        var fetch = _harness.Source.Hold = new Gate();
        var options = _harness.Options();
        _manager = _harness.Manager(options);
        _sweeper = new WatchSessionSweeper(
            _manager,
            Options.Create(options),
            _harness.Time,
            NullLogger<WatchSessionSweeper>.Instance
        );
        _sweeper.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        _manager.Heartbeat(Harness.Video, 0.0);
        fetch.WaitUntilEntered();

        // Sweeps well inside the idle timeout. The loop may start its timer
        // late (see above), so step the clock a second at a time with a pause
        // for it to catch up, stopping a minute short of the timeout.
        for (var second = 0; second < 60; second++)
        {
            _harness.Time.Advance(TimeSpan.FromSeconds(1));
            Thread.Sleep(1);
        }
    }

    public void Dispose()
    {
        _sweeper.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _sweeper.Dispose();
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void KeepsTheSession() => _manager.Count.ShouldBe(1);

    [Fact]
    public void LeavesItWorking() =>
        _manager.Find(Harness.Video)!.State.ShouldBe(WatchState.Fetching);
}

public sealed class WhenTheSweeperIsStopped : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSessionManager _manager;
    private readonly WatchSessionSweeper _sweeper;
    private readonly Task _stopped;

    public WhenTheSweeperIsStopped()
    {
        var options = _harness.Options();
        _manager = _harness.Manager(options);
        _sweeper = new WatchSessionSweeper(
            _manager,
            Options.Create(options),
            _harness.Time,
            NullLogger<WatchSessionSweeper>.Instance
        );
        _sweeper.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        _stopped = _sweeper.StopAsync(CancellationToken.None);
        Waits.On(_stopped);
    }

    public void Dispose()
    {
        _sweeper.Dispose();
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void StopsCleanly() => _stopped.IsCompletedSuccessfully.ShouldBeTrue();

    [Fact]
    public void EndsItsLoop() => _sweeper.ExecuteTask!.IsCompleted.ShouldBeTrue();
}
