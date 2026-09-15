using FoulFilterNet.Domain;

namespace FoulFilterNet.SmartCut.Tests;

/// <summary>
/// The context window from <c>Legacy/src/test_pipeline_logic.py</c>, the same one
/// <c>SmartCutMapperTests</c> uses, so prompt and mapping assertions line up.
/// </summary>
internal static class PromptWindow
{
    public static readonly IReadOnlyList<Word> Words =
    [
        new Word("just", 5.0, 5.2),
        new Word("tell", 5.2, 5.4),
        new Word("him", 5.4, 5.5),
        new Word("to", 5.5, 5.6),
        new Word("go", 5.6, 5.8),
        new Word("to", 5.8, 5.9),
        new Word("hell", 5.9, 6.2),
    ];
}

/// <summary>
/// The ordinary case: <c>remove</c> on audio, where ADR-0004 permits the advisor
/// to widen the cut. The prompt must be <c>PROMPT_TEMPLATE</c> with an empty
/// <c>extra_rule</c>.
/// </summary>
public class WhenBuildingAPromptThatMayWiden
{
    private readonly string _prompt;

    public WhenBuildingAPromptThatMayWiden()
    {
        _prompt = SmartCutPrompt.Build(PromptWindow.Words, "hell", allowWidening: true);

        _prompt.ShouldNotBeNullOrWhiteSpace();
        _prompt.ShouldNotContain("{extra_rule}");
        _prompt.ShouldNotContain("{indexed_text}");
        _prompt.ShouldNotContain("{target_word}");
    }

    [Fact]
    public void OpensWithThePythonsTaskStatement() =>
        _prompt.ShouldStartWith(
            "\n    TASK: You are a video editor. Your goal is to remove profanity in the least noticable manner.");

    [Fact]
    public void OmitsTheSurgicalRule() => _prompt.ShouldNotContain("SURGICAL MODE");

    [Fact]
    public void LeavesTheExtraRuleLineBlank() =>
        _prompt.ShouldContain("5. THEMATIC: For \"gay/bisexual\" content, remove the entire section.\n    \n");

    [Fact]
    public void CarriesAllEightWorkedExamples() =>
        _prompt.Split("\n").Count(line => line.TrimStart().StartsWith("- EX ", StringComparison.Ordinal))
            .ShouldBe(8);

    [Fact]
    public void KeepsTheFirstWorkedExampleVerbatim() =>
        _prompt.ShouldContain(
            "    - EX 1: SURGICAL REMOVAL (Mid-stream)\n" +
            "      Input: 0: I, 1: really, 2: think, 3: that, 4: this, 5: is, 6: a, 7: damn, 8: good, 9: movie, 10: from, 11: what, 12: I\n" +
            "      Target: \"damn\"\n" +
            "      Output: {\"start_index\": 7, \"end_index\": 7}\n");

    [Fact]
    public void KeepsTheLastWorkedExamplesMultiLineOutputVerbatim() =>
        _prompt.ShouldContain(
            "    - EX 8: IDIOMATIC PHRASE IN LARGE WINDOW\n" +
            "      Input: 0: and, 1: was, 2: and, 3: then, 4: he, 5: said, 6: that, 7: anyway, 8: it, 9: was, 10: crazy, 11: just, 12: tell, 13: him, 14: to, 15: go, 16: to, 17: hell, 18: because, 19: I, 20: don't, 21: care, 22: about\n" +
            "      Target: \"hell\"\n" +
            "      Output: {\n" +
            "          \"reasoning\": \"Target 'hell' is part of the idiom 'go to hell'. Cutting the entire phrase (indices 11-17) for a natural transition.\",\n" +
            "          \"start_index\": 11,\n" +
            "          \"end_index\": 17\n" +
            "      }\n");

    [Fact]
    public void KeepsTheDoubleSpaceTyPoInTheThirdExample() =>
        _prompt.ShouldContain("11: they, 12: drove,  13: away");

    [Fact]
    public void InterpolatesTheTargetPhrase() => _prompt.ShouldContain("TARGET WORD: \"hell\"");

    [Fact]
    public void RendersTheContextWindowIndexedFromZero() =>
        _prompt.ShouldContain(
            "    CURRENT SEQUENCE:\n    0: just\n1: tell\n2: him\n3: to\n4: go\n5: to\n6: hell\n    TARGET WORD:");

    [Fact]
    public void ClosesWithTheResponseSchema() =>
        _prompt.ShouldEndWith(
            "    Return ONLY JSON in this format:\n" +
            "    {\n" +
            "    \"reasoning\": \"Brief explanation of why these indices were chosen\",\n" +
            "    \"start_index\": int,\n" +
            "    \"end_index\": int\n" +
            "    }\n    ");

    [Fact]
    public void UsesUnixNewlinesWhateverTheCheckoutDid() => _prompt.ShouldNotContain("\r");
}

/// <summary>
/// Silence, bleep and every video edit (ADR-0004). The surgical rule is appended
/// so the model is told widening is forbidden, as well as being clamped later by
/// <see cref="SmartCutMapper"/>.
/// </summary>
public class WhenBuildingAPromptThatMayNotWiden
{
    private const string SurgicalRule =
        "4. SURGICAL MODE: Widening is FORBIDDEN for this edit. Return " +
        "start_index == end_index == the target index exactly, unless rule 5 applies.";

    private readonly string _prompt;
    private readonly string _wideningPrompt;

    public WhenBuildingAPromptThatMayNotWiden()
    {
        _prompt = SmartCutPrompt.Build(PromptWindow.Words, "hell", allowWidening: false);
        _wideningPrompt = SmartCutPrompt.Build(PromptWindow.Words, "hell", allowWidening: true);

        _prompt.ShouldNotBeNullOrWhiteSpace();
        _prompt.ShouldNotContain("{extra_rule}");
    }

    [Fact]
    public void AppendsTheSurgicalRuleVerbatim() => _prompt.ShouldContain(SurgicalRule);

    [Fact]
    public void ExposesTheRuleTextAsAConstant() => SmartCutPrompt.SurgicalRule.ShouldBe(SurgicalRule);

    [Fact]
    public void PlacesItWhereThePythonPutIt() =>
        _prompt.ShouldContain(
            "5. THEMATIC: For \"gay/bisexual\" content, remove the entire section.\n    " + SurgicalRule + "\n");

    [Fact]
    public void ChangesNothingElseAboutThePrompt() =>
        _prompt.Replace(SurgicalRule, string.Empty, StringComparison.Ordinal).ShouldBe(_wideningPrompt);
}

public class WhenRenderingTheIndexedContextWindow
{
    private readonly string _rendered;

    public WhenRenderingTheIndexedContextWindow()
    {
        _rendered = SmartCutPrompt.RenderIndexedWindow(PromptWindow.Words);

        _rendered.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void NumbersFromZero() => _rendered.ShouldStartWith("0: just");

    [Fact]
    public void WritesOneLinePerWord() => _rendered.Split("\n").Length.ShouldBe(PromptWindow.Words.Count);

    [Fact]
    public void UsesTheWordsOwnTextRatherThanTheNormalizedForm() =>
        SmartCutPrompt.RenderIndexedWindow([new Word("Hell,", 1.0, 1.2)]).ShouldBe("0: Hell,");

    [Fact]
    public void RendersAnEmptyWindowAsAnEmptyString() =>
        SmartCutPrompt.RenderIndexedWindow([]).ShouldBeEmpty();
}
