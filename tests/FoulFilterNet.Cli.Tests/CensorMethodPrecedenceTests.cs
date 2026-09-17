using FoulFilterNet.Cli;
using FoulFilterNet.Domain;

namespace FoulFilterNet.Cli.Tests;

/// <summary>
/// Shared arrangement for the precedence matrix: one command line, parsed for
/// real, resolved against a fake environment.
/// </summary>
internal static class Resolve
{
    /// <summary>The two positional arguments every invocation carries.</summary>
    internal static readonly string[] Required = ["clip.mp3", "bad_words.txt"];

    internal static CensorMethod Method(string? environmentValue, params string[] flags) =>
        Method(Environment(environmentValue), flags);

    internal static CensorMethod Method(Func<string, string?> environment, params string[] flags)
    {
        var commandLine = new FoulFilterCommandLine();
        var parsed = commandLine.Parse([.. Required, .. flags]);

        parsed.Errors.ShouldBeEmpty();

        return commandLine.ResolveCensorMethod(parsed, environment);
    }

    internal static Func<string, string?> Environment(string? censorMethod) =>
        key => key == Pipeline.ConfigurationKeys.CensorMethod ? censorMethod : null;
}

/// <summary>
/// The top of the Python's chain: an explicit <c>--censor_method</c> wins over
/// every shorthand flag and over the environment.
/// </summary>
public sealed class WhenCensorMethodIsGivenAlongsideBleepAndDelete
{
    private readonly CensorMethod _resolved =
        Resolve.Method("remove", "--censor_method", "silence", "--bleep", "--delete");

    [Fact]
    public void TakesTheExplicitMethod() => _resolved.ShouldBe(CensorMethod.Silence);
}

/// <summary>The second rung: <c>--bleep</c> beats <c>--delete</c>.</summary>
public sealed class WhenBleepAndDeleteAreBothGiven
{
    private readonly CensorMethod _resolved = Resolve.Method("silence", "--bleep", "--delete");

    [Fact]
    public void Bleeps() => _resolved.ShouldBe(CensorMethod.Bleep);
}

/// <summary>The third rung: <c>--delete</c> beats the environment default.</summary>
public sealed class WhenOnlyDeleteIsGiven
{
    private readonly CensorMethod _resolved = Resolve.Method("bleep", "--delete");

    [Fact]
    public void Removes() => _resolved.ShouldBe(CensorMethod.Remove);
}

/// <summary>The bottom rung: with no flag at all the environment decides.</summary>
public sealed class WhenNoCensorFlagIsGiven
{
    private readonly CensorMethod _fromEnvironment = Resolve.Method("bleep");
    private readonly CensorMethod _shouted = Resolve.Method("BLEEP");

    [Fact]
    public void TakesTheEnvironmentValue() => _fromEnvironment.ShouldBe(CensorMethod.Bleep);

    [Fact]
    public void ReadsTheEnvironmentCaseInsensitively() => _shouted.ShouldBe(CensorMethod.Bleep);
}

/// <summary>
/// <c>CENSOR_METHOD=delete</c> is the spelling the legacy deployment used for
/// what is now <see cref="CensorMethod.Remove"/>.
/// </summary>
public sealed class WhenTheEnvironmentAsksForTheLegacyDeleteSynonym
{
    private readonly CensorMethod _resolved = Resolve.Method("delete");

    [Fact]
    public void Removes() => _resolved.ShouldBe(CensorMethod.Remove);
}

/// <summary>
/// The same synonym as an option value. The Python's <c>choices</c> list would
/// have rejected it, which made the flag and the environment disagree about
/// what "delete" meant; the port accepts it in both places.
/// </summary>
public sealed class WhenCensorMethodIsTheLegacyDeleteSynonym
{
    private readonly CensorMethod _resolved = Resolve.Method(environmentValue: null, "--censor_method", "delete");

    [Fact]
    public void Removes() => _resolved.ShouldBe(CensorMethod.Remove);
}

/// <summary>Nothing set anywhere - the Python's <c>os.getenv(..., "silence")</c>.</summary>
public sealed class WhenNothingSelectsACensorMethod
{
    private readonly CensorMethod _resolved = Resolve.Method(environmentValue: null);

    [Fact]
    public void Silences() => _resolved.ShouldBe(CensorMethod.Silence);
}

/// <summary>
/// An environment value no build understands. The Python handed it straight to
/// the pipeline, which treated anything unrecognised as silence; failing the run
/// over a stale variable would be a behaviour change.
/// </summary>
public sealed class WhenTheEnvironmentCensorMethodIsUnrecognised
{
    private readonly CensorMethod _resolved = Resolve.Method("obliterate");

    [Fact]
    public void FallsBackToSilence() => _resolved.ShouldBe(CensorMethod.Silence);
}

/// <summary>
/// The environment is read through the same lookup the host binds to
/// configuration, so an empty variable is absent rather than unrecognised.
/// </summary>
public sealed class WhenTheEnvironmentCensorMethodIsBlank
{
    private readonly CensorMethod _resolved = Resolve.Method("   ");

    [Fact]
    public void FallsBackToSilence() => _resolved.ShouldBe(CensorMethod.Silence);
}
