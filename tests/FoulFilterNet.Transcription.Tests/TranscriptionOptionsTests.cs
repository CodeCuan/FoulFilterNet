using FoulFilterNet.Transcription;

namespace FoulFilterNet.Transcription.Tests;

public class WhenTranscriptionOptionsAreLeftAtTheirDefaults
{
    private readonly TranscriptionOptions _options;

    public WhenTranscriptionOptionsAreLeftAtTheirDefaults()
    {
        _options = new TranscriptionOptions();

        _options.Model.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void UsesTheBaseModel() => _options.Model.ShouldBe("openai/whisper-base");

    [Fact]
    public void DetectsTheLanguageRatherThanForcingOne() => _options.Language.ShouldBeNull();

    [Fact]
    public void LetsTheEngineChooseTheDevice() =>
        _options.Device.ShouldBe(TranscriptionDevice.Auto);

    [Fact]
    public void KeepsTheModelLoadedBetweenJobs() => _options.UnloadAfterJob.ShouldBeFalse();
}

public class WhenTranscriptionOptionsCarryABareModelSize
{
    private readonly TranscriptionOptions _options;

    public WhenTranscriptionOptionsCarryABareModelSize()
    {
        _options = new TranscriptionOptions { Model = "large-v3-turbo" };

        _options.Model.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void NormalizesItOnTheWayIn() =>
        _options.Model.ShouldBe("openai/whisper-large-v3-turbo");

    [Fact]
    public void NormalizesAnEmptyModelBackToTheDefault() =>
        new TranscriptionOptions { Model = "" }.Model.ShouldBe("openai/whisper-base");

    [Fact]
    public void LeavesASlashQualifiedNameAlone() =>
        new TranscriptionOptions { Model = "openai/whisper-tiny" }.Model.ShouldBe(
            "openai/whisper-tiny"
        );

    [Fact]
    public void NormalizesAgainWhenCopiedWithANewModel() =>
        (_options with { Model = "small" }).Model.ShouldBe("openai/whisper-small");
}

public class WhenTranscriptionOptionsPinADeviceAndLanguage
{
    private readonly TranscriptionOptions _options;

    public WhenTranscriptionOptionsPinADeviceAndLanguage()
    {
        _options = new TranscriptionOptions
        {
            Language = "en",
            Device = TranscriptionDevice.Cpu,
            UnloadAfterJob = true,
        };

        _options.Model.ShouldBe("openai/whisper-base");
    }

    [Fact]
    public void KeepsTheRequestedLanguage() => _options.Language.ShouldBe("en");

    [Fact]
    public void KeepsTheRequestedDevice() => _options.Device.ShouldBe(TranscriptionDevice.Cpu);

    [Fact]
    public void KeepsTheUnloadPreference() => _options.UnloadAfterJob.ShouldBeTrue();

    [Fact]
    public void TreatsABlankLanguageAsAutoDetect() =>
        new TranscriptionOptions { Language = "   " }.Language.ShouldBeNull();
}

public class WhenTranscriptionOptionsLeaveThePriorityWordPassAlone
{
    private readonly TranscriptionOptions _options = new();

    [Fact]
    public void RunsThePass() => _options.PriorityPass.ShouldBeTrue();

    [Fact]
    public void LeavesTheListPathToTheDataDirectory() => _options.PriorityWordsPath.ShouldBeNull();
}

public class WhenTranscriptionOptionsConfigureThePriorityWordPass
{
    private readonly TranscriptionOptions _options = new()
    {
        PriorityPass = false,
        PriorityWordsPath = "  lists/priority.txt  ",
    };

    [Fact]
    public void CanTurnThePassOff() => _options.PriorityPass.ShouldBeFalse();

    [Fact]
    public void TrimsTheListPath() => _options.PriorityWordsPath.ShouldBe("lists/priority.txt");

    [Fact]
    public void TreatsABlankListPathAsUnset() =>
        new TranscriptionOptions { PriorityWordsPath = "   " }.PriorityWordsPath.ShouldBeNull();
}
