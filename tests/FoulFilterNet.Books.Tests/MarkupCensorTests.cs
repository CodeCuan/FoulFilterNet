using FoulFilterNet.Domain;

namespace FoulFilterNet.Books.Tests;

public class MarkupCensorTests
{
    private readonly MarkupCensor _censor = new(
        new TextMatcher(BadWordsList.FromLines(["damn", "go to hell", "class"]), ["fuck"]),
        "****"
    );
    private readonly BookTally _tally = new();

    private string Censor(string markup, IReadOnlySet<string>? onlyInside = null) =>
        _censor.Censor(markup, _tally, onlyInside);

    [Fact]
    public void TextIsMaskedAndMarkupKept() =>
        Censor("<p class=\"a\">Well damn it</p>").ShouldBe("<p class=\"a\">Well **** it</p>");

    [Fact]
    public void AttributesAreNotTouched() =>
        Censor("<p class=\"class\">ok</p>").ShouldBe("<p class=\"class\">ok</p>");

    [Fact]
    public void ADropCapSplitAcrossInlineTagsIsMasked() =>
        Censor("<p><span class=\"dc\">D</span>amn it</p>")
            .ShouldBe("<p><span class=\"dc\">****</span> it</p>");

    [Fact]
    public void APhraseAcrossInlineTagsIsMasked() =>
        Censor("<p>go <i>to</i> hell</p>").ShouldBe("<p>****<i></i></p>");

    [Fact]
    public void BlockTagsSeparateWords() =>
        Censor("<p>da</p><p>mn</p>").ShouldBe("<p>da</p><p>mn</p>");

    [Fact]
    public void ALineBreakSeparatesWords() =>
        Censor("<p>da<br/>mn</p>").ShouldBe("<p>da<br/>mn</p>");

    [Fact]
    public void AWordWrittenWithAnEntityIsMasked() =>
        Censor("<p>d&#97;mn</p>").ShouldBe("<p>****</p>");

    [Fact]
    public void AnEntityElsewhereSurvives() =>
        Censor("<p>R&amp;D damn&nbsp;it</p>").ShouldBe("<p>R&amp;D ****&nbsp;it</p>");

    [Fact]
    public void ASoftHyphenDoesNotHideAWord() => Censor("<p>da­mn</p>").ShouldBe("<p>****</p>");

    [Fact]
    public void ScriptsAndStylesAreLeftAlone() =>
        Censor("<style>.damn{}</style><script>damn</script>")
            .ShouldBe("<style>.damn{}</style><script>damn</script>");

    [Fact]
    public void CommentsAndTheDoctypeAreLeftAlone() =>
        Censor("<!DOCTYPE damn><!-- damn --><?damn?><p>x</p>")
            .ShouldBe("<!DOCTYPE damn><!-- damn --><?damn?><p>x</p>");

    [Fact]
    public void AGreaterThanInsideAnAttributeDoesNotEndTheTag() =>
        Censor("<p title=\"a>damn\">x</p>").ShouldBe("<p title=\"a>damn\">x</p>");

    [Fact]
    public void AMaskIsEscapedForMarkup()
    {
        var censor = new MarkupCensor(new TextMatcher(BadWordsList.FromLines(["damn"]), []), "<&>");

        censor.Censor("<p>damn</p>", _tally, null).ShouldBe("<p>&lt;&amp;&gt;</p>");
    }

    [Fact]
    public void OnlyNamedElementsAreCensoredWhenAsked() =>
        Censor(
                "<dc:identifier>damn</dc:identifier><dc:description>damn</dc:description>",
                new HashSet<string>(["dc:description"])
            )
            .ShouldBe("<dc:identifier>damn</dc:identifier><dc:description>****</dc:description>");

    [Fact]
    public void TheTallyCountsMasksAndNearMisses()
    {
        Censor("<p>damn fucker damned</p>");

        _tally.Masked.ShouldBe(2);
        _tally.NearMisses.ShouldBe(
            new Dictionary<(string, string), int> { [("damned", "damn")] = 1 }
        );
    }
}
