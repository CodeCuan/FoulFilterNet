using FoulFilterNet.Domain;
using FoulFilterNet.Pipeline;
using FoulFilterNet.Transcription;
using Microsoft.Extensions.Configuration;

namespace FoulFilterNet.Pipeline.Tests;

/// <summary>
/// Both hosts bind the <c>Transcription</c> section onto
/// <see cref="TranscriptionOptions"/>, so the keys must spell its properties.
/// </summary>
public sealed class WhenThePriorityWordPassIsConfiguredByKey
{
    private readonly TranscriptionOptions _options;

    public WhenThePriorityWordPassIsConfiguredByKey()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [ConfigurationKeys.PriorityPass] = "false",
                    [ConfigurationKeys.PriorityWordsPath] = "/srv/priority.txt",
                }
            )
            .Build();

        _options =
            configuration.GetSection("Transcription").Get<TranscriptionOptions>()
            ?? throw new InvalidOperationException("The section did not bind.");
    }

    [Fact]
    public void BindsTheSwitch() => _options.PriorityPass.ShouldBeFalse();

    [Fact]
    public void BindsTheListPath() => _options.PriorityWordsPath.ShouldBe("/srv/priority.txt");
}

/// <summary>
/// The Priority Word Pass's tuning is configuration too (X05): five keys in the
/// same section, each of which must spell a <see cref="TranscriptionOptions"/>
/// property, and each of which a host turns into a <see cref="PriorityTuning"/>.
/// </summary>
public sealed class WhenThePriorityTuningIsConfiguredByKey
{
    private readonly PriorityTuning _tuning;

    public WhenThePriorityTuningIsConfiguredByKey()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [ConfigurationKeys.PriorityPaddingPre] = "0.4",
                    [ConfigurationKeys.PriorityPaddingPost] = "0.9",
                    [ConfigurationKeys.PriorityMinimumCutSeconds] = "1.2",
                    [ConfigurationKeys.PrioritySubWindowSeconds] = "4",
                    [ConfigurationKeys.PrioritySubWindowStepSeconds] = "1",
                }
            )
            .Build();

        _tuning = PriorityTuning.ForOptions(
            configuration.GetSection("Transcription").Get<TranscriptionOptions>()
                ?? throw new InvalidOperationException("The section did not bind.")
        );
    }

    [Fact]
    public void BindsThePreRoll() => _tuning.Padding.Pre.ShouldBe(0.4);

    [Fact]
    public void BindsThePostRoll() => _tuning.Padding.Post.ShouldBe(0.9);

    [Fact]
    public void BindsTheMinimumCut() => _tuning.MinimumCutSeconds.ShouldBe(1.2);

    [Fact]
    public void BindsTheSubWindowLength() => _tuning.SubWindows.LengthSeconds.ShouldBe(4.0);

    [Fact]
    public void BindsTheSubWindowStep() => _tuning.SubWindows.StepSeconds.ShouldBe(1.0);

    [Fact]
    public void PadsPriorityWordsAsConfigured() =>
        _tuning
            .PaddingFor(PriorityWordList.Default)
            .Widen("fuck", 10.0, 10.01)
            .ShouldBe((10.01 - 1.2 - 0.4, 10.01 + 0.9));
}

/// <summary>
/// A section that names none of the tuning keys - every host's default - is the
/// tuning ADR-0008 measured, so behaviour out of the box is what X03 and X04
/// left it.
/// </summary>
public sealed class WhenThePriorityTuningIsNotConfiguredAtAll
{
    private readonly TranscriptionOptions _options =
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { [ConfigurationKeys.PriorityPass] = "true" }
            )
            .Build()
            .GetSection("Transcription")
            .Get<TranscriptionOptions>()
        ?? new TranscriptionOptions();

    [Fact]
    public void IsTheShippedTuning() =>
        PriorityTuning.ForOptions(_options).ShouldBe(PriorityTuning.Default);

    [Fact]
    public void CutsPriorityWordsAsX03LeftThem() =>
        PriorityTuning
            .ForOptions(_options)
            .PaddingFor(PriorityWordList.Default)
            .Widen("fuck", 10.0, 10.01)
            .ShouldBe(
                CutPadding.ForPriorityWords(PriorityWordList.Default).Widen("fuck", 10.0, 10.01)
            );
}

public sealed class WhenReadingThePriorityWordFile : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"ffn_x01_{Guid.NewGuid():N}"
    );

    public WhenReadingThePriorityWordFile()
    {
        Directory.CreateDirectory(_directory);
    }

    private string File(string name, params string[] lines)
    {
        var path = Path.Combine(_directory, name);
        System.IO.File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void UsesTheBuiltInListWhenTheFileIsMissing() =>
        PriorityWordFile
            .Read(Path.Combine(_directory, "priority_words.txt"))
            .ShouldBeSameAs(PriorityWordList.Default);

    [Fact]
    public void ReadsTheFileInTheBadWordsFormat() =>
        PriorityWordFile
            .Read(File("priority_words.txt", "# mine", "Shit", "fuck"))
            .Words.ShouldBe(["shit", "fuck"]);

    [Fact]
    public void ReadsAnEmptyFileAsAnEmptyList() =>
        PriorityWordFile.Read(File("priority_words.txt")).IsEmpty.ShouldBeTrue();

    [Fact]
    public void ResolvesTheFileInTheDataDirectoryByDefault() =>
        PriorityWordFile
            .ForOptions(new TranscriptionOptions(), _directory)
            .ShouldBeSameAs(PriorityWordList.Default);

    [Fact]
    public void ResolvesAFileInTheDataDirectory()
    {
        File("priority_words.txt", "shit");

        PriorityWordFile
            .ForOptions(new TranscriptionOptions(), _directory)
            .Words.ShouldBe(["shit"]);
    }

    [Fact]
    public void ResolvesAConfiguredPath() =>
        PriorityWordFile
            .ForOptions(
                new TranscriptionOptions { PriorityWordsPath = File("mine.txt", "damn") },
                _directory
            )
            .Words.ShouldBe(["damn"]);

    [Fact]
    public void GivesAnEmptyListWhenThePassIsOff() =>
        PriorityWordFile
            .ForOptions(new TranscriptionOptions { PriorityPass = false }, _directory)
            .IsEmpty.ShouldBeTrue();

    [Fact]
    public void SaysTheBuiltInListCameFromNoFile() =>
        PriorityWordFile
            .Resolve(new TranscriptionOptions(), _directory)
            .Origin.ShouldBe(PriorityWordSource.BuiltInOrigin);

    [Fact]
    public void SaysWhichFileTheListCameFrom()
    {
        var path = File("priority_words.txt", "shit");

        PriorityWordFile.Resolve(new TranscriptionOptions(), _directory).Origin.ShouldBe(path);
    }

    [Fact]
    public void SaysThePassWasSwitchedOff() =>
        PriorityWordFile
            .Resolve(new TranscriptionOptions { PriorityPass = false }, _directory)
            .Origin.ShouldBe(PriorityWordSource.SwitchedOffOrigin);

    [Fact]
    public void GivesTheResolvedListItsWords()
    {
        File("priority_words.txt", "shit", "fuck");

        PriorityWordFile
            .Resolve(new TranscriptionOptions(), _directory)
            .Words.Words.ShouldBe(["shit", "fuck"]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is untidy, not a test failure.
        }
    }
}
