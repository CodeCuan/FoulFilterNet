using Whisper.net;
using Whisper.net.Ggml;

namespace FoulFilterNet.Transcription.Tests;

/// <summary>
/// Configuration carries Hugging Face repository ids because that is what the
/// Python carried (<see cref="ModelNames"/>), but whisper.cpp wants a GGML
/// weights file. The mapping happens here, at the engine's edge, so the
/// configuration spelling never has to change.
/// </summary>
public sealed class WhenNamingTheWeightsFileForAModel
{
    [Fact]
    public void NamesTheDefaultModelsFile() =>
        WhisperModelFiles.FileName(ModelNames.Default).ShouldBe("ggml-base.bin");

    [Fact]
    public void NamesTheTurboModelsFile() =>
        WhisperModelFiles
            .FileName("openai/whisper-large-v3-turbo")
            .ShouldBe("ggml-large-v3-turbo.bin");

    [Fact]
    public void KeepsTheEnglishOnlyVariantDistinct() =>
        WhisperModelFiles.FileName("openai/whisper-base.en").ShouldBe("ggml-base.en.bin");

    [Fact]
    public void AcceptsABareSizeTheWayConfigurationMayStillSpellIt() =>
        WhisperModelFiles.FileName("small").ShouldBe("ggml-small.bin");

    [Fact]
    public void NamesAThirdPartyRepositorysFileAfterItsModel() =>
        WhisperModelFiles
            .FileName("distil-whisper/distil-large-v3")
            .ShouldBe("ggml-distil-large-v3.bin");

    [Fact]
    public void RefusesAMissingModelName() =>
        Should.Throw<ArgumentException>(() => WhisperModelFiles.FileName("  "));
}

/// <summary>
/// The same mapping in the direction acquisition needs: Whisper.net can fetch a
/// model it has a <see cref="GgmlType"/> for, and nothing else.
/// </summary>
public sealed class WhenAskingWhichGgmlModelAConfiguredNameIs
{
    [Fact]
    public void RecognisesTheDefault() =>
        WhisperModelFiles.GgmlTypeFor(ModelNames.Default).ShouldBe(GgmlType.Base);

    [Fact]
    public void RecognisesTheTurboModel() =>
        WhisperModelFiles
            .GgmlTypeFor("openai/whisper-large-v3-turbo")
            .ShouldBe(GgmlType.LargeV3Turbo);

    [Fact]
    public void RecognisesAnEnglishOnlyVariant() =>
        WhisperModelFiles.GgmlTypeFor("openai/whisper-small.en").ShouldBe(GgmlType.SmallEn);

    [Fact]
    public void RecognisesTheOlderLargeModels() =>
        WhisperModelFiles.GgmlTypeFor("openai/whisper-large-v2").ShouldBe(GgmlType.LargeV2);

    [Fact]
    public void AdmitsItCannotFetchAThirdPartyModel() =>
        WhisperModelFiles.GgmlTypeFor("distil-whisper/distil-large-v3").ShouldBeNull();
}

/// <summary>
/// DTW word timestamps are the ones worth having (ADR-0006), and whisper.cpp
/// will only produce them for a model whose attention heads it knows. Asking for
/// them without a matching preset is the one way to configure DTW wrongly, so
/// the preset is derived from the model rather than configured beside it.
/// </summary>
public sealed class WhenAskingWhichAlignmentHeadsAModelHas
{
    [Fact]
    public void KnowsTheDefaultModelsHeads() =>
        WhisperModelFiles
            .AlignmentHeadsFor(ModelNames.Default)
            .ShouldBe(WhisperAlignmentHeadsPreset.Base);

    [Fact]
    public void KnowsTheTurboModelsHeads() =>
        WhisperModelFiles
            .AlignmentHeadsFor("openai/whisper-large-v3-turbo")
            .ShouldBe(WhisperAlignmentHeadsPreset.LargeV3Turbo);

    [Fact]
    public void KnowsAnEnglishOnlyVariantsHeads() =>
        WhisperModelFiles
            .AlignmentHeadsFor("openai/whisper-small.en")
            .ShouldBe(WhisperAlignmentHeadsPreset.SmallEn);

    [Fact]
    public void KnowsOfNoneForAThirdPartyModelSoDtwStaysOff() =>
        WhisperModelFiles.AlignmentHeadsFor("distil-whisper/distil-large-v3").ShouldBeNull();
}
