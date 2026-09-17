using FoulFilterNet.Transcription;

namespace FoulFilterNet.Transcription.Tests;

public class WhenNormalizingABareModelSize
{
    private readonly string _normalized;

    public WhenNormalizingABareModelSize()
    {
        _normalized = ModelNames.Normalize("base");

        _normalized.ShouldNotBeNullOrWhiteSpace();
        _normalized.ShouldContain("/");
    }

    [Fact]
    public void ExpandsToTheFullModelId() => _normalized.ShouldBe("openai/whisper-base");

    [Fact]
    public void ExpandsEveryOtherSizeTheSameWay() =>
        ModelNames.Normalize("large-v3-turbo").ShouldBe("openai/whisper-large-v3-turbo");

    [Fact]
    public void TrimsSurroundingWhitespaceBeforeExpanding() =>
        ModelNames.Normalize("  small  ").ShouldBe("openai/whisper-small");
}

public class WhenNormalizingASlashQualifiedModelName
{
    private readonly string _normalized;

    public WhenNormalizingASlashQualifiedModelName()
    {
        _normalized = ModelNames.Normalize("openai/whisper-large-v3-turbo");

        _normalized.ShouldNotBeNullOrWhiteSpace();
        _normalized.ShouldContain("/");
    }

    [Fact]
    public void PassesItThroughUntouched() => _normalized.ShouldBe("openai/whisper-large-v3-turbo");

    [Fact]
    public void DoesNotPrefixAThirdPartyRepository() =>
        ModelNames
            .Normalize("distil-whisper/distil-large-v3")
            .ShouldBe("distil-whisper/distil-large-v3");

    [Fact]
    public void StillTrimsWhitespace() =>
        ModelNames.Normalize(" openai/whisper-tiny ").ShouldBe("openai/whisper-tiny");
}

public class WhenNoModelNameIsSupplied
{
    private readonly string _fromNull;

    public WhenNoModelNameIsSupplied()
    {
        _fromNull = ModelNames.Normalize(null);

        _fromNull.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void FallsBackToBase() => _fromNull.ShouldBe("openai/whisper-base");

    [Fact]
    public void TreatsAnEmptyStringTheSameWay() =>
        ModelNames.Normalize("").ShouldBe("openai/whisper-base");

    [Fact]
    public void TreatsWhitespaceTheSameWay() =>
        ModelNames.Normalize("   ").ShouldBe("openai/whisper-base");

    [Fact]
    public void IsTheDeclaredDefault() => _fromNull.ShouldBe(ModelNames.Default);
}
