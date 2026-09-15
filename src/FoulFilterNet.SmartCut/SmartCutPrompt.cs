using FoulFilterNet.Domain;

namespace FoulFilterNet.SmartCut;

/// <summary>
/// Builds the few-shot prompt Smart Cut sends to an LLM. Ported verbatim from
/// <c>PROMPT_TEMPLATE</c> and <c>SURGICAL_RULE</c> in
/// <c>Legacy/src/ai_helper.py</c>, worked examples and all: those examples are the
/// only thing teaching a small local model the difference between a surgical cut
/// and an idiomatic phrase, so their wording is load-bearing and must not be
/// tidied up.
/// </summary>
public static class SmartCutPrompt
{
    /// <summary>
    /// Appended when ADR-0004 forbids widening. Numbered "4" and placed after
    /// rule 5 exactly as the Python did - renumbering it would change the prompt.
    /// </summary>
    public const string SurgicalRule =
        "4. SURGICAL MODE: Widening is FORBIDDEN for this edit. Return " +
        "start_index == end_index == the target index exactly, unless rule 5 applies.";

    /// <summary>
    /// The template, with the Python's <c>str.format</c> placeholders intact. The
    /// doubled braces are already unescaped, so the literal JSON in the examples
    /// reads exactly as the model will see it.
    /// </summary>
    private const string TemplateSource = """

            TASK: You are a video editor. Your goal is to remove profanity in the least noticable manner. You are supplied with a list of words and their timestamps.
        
            CRITICAL RULES:
            1. SURGICAL PRECISION: Cut ONLY the target word index unless it's a phrase-level rule.
            2. ADJECTIVE PROTECTION: Do not cut adjectives like "good", "great", "very", or "movie" even if they follow a swear.
            3. PHRASE RULES: If a target is part of an idiomatic phrase (e.g., "go to hell") or a stuttered thought (e.g., "just tell him to..."), cut the entire leading context back to the last clean break point.
            4. FALSE POSITIVES: If "hoe" is a tool or "bitch" is a dog, return -1 for the start and end.
            5. THEMATIC: For "gay/bisexual" content, remove the entire section.
            {extra_rule}
        
            EXAMPLES:
            - EX 1: SURGICAL REMOVAL (Mid-stream)
              Input: 0: I, 1: really, 2: think, 3: that, 4: this, 5: is, 6: a, 7: damn, 8: good, 9: movie, 10: from, 11: what, 12: I
              Target: "damn"
              Output: {"start_index": 7, "end_index": 7}
        
            - EX 2: THEMATIC PHRASE (Sexual Orientation - Seamless Erasure)
              Input: 0: was, 1: a, 2: garbage, 3: collector, 4: he, 5: turned, 6: homosexual, 7: at, 8: fourteen, 9: John, 10: was, 11: a, 12: an, 13: accountant, 14: who
              Target: "homosexuals"
              Output: {"start_index": 4, "end_index": 8}
        
            - EX 3: ACTION REMOVAL (Clean Narrative Jump)
              Input: 0: standing, 1: by, 2: the, 3: car, 4: then, 5: the, 6: two, 7: gay, 8: men, 9: kissed, 10: then, 11: they, 12: drove,  13: away
              Target: "gay"
              Output: {"start_index": 5, "end_index": 9}
        
            - EX 4: PLOT-CRITICAL PROFANITY (Action/Subject)
              Input: 0: security, 1: footage, 2: clearly, 3: shows, 4: how, 5: the, 6: bitch, 7: stole, 8: the, 9: keys, 10: before, 11: running, 12: away
              Target: "bitch"
              Output: {"start_index": 5, "end_index": 6}
        
            - EX 5: IDIOMATIC PHRASE (Complete thought)
              Input: 0: told, 1: him, 2: stay, 3: away, 4: but, 5: he, 6: wouldn't, 7: so, 8: go, 9: to, 10: hell, 11: is, 12: what, 13: I, 14: said, 15: John, 16: farted, 17: continuously
              Target: "hell"
              Output: {"start_index": 8, "end_index": 10}
        
            - EX 6: FALSE POSITIVE (Gardening/Tool)
              Input: 0: after, 1: the, 2: rain, 3: stopped, 4: the, 5: farmer, 6: grabbed, 7: a, 8: hoe, 9: to, 10: fix, 11: the, 12: flower, 13: beds
              Target: "hoe"
              Output: {"start_index": -1, "end_index": -1}
        
            - EX 7: MID-WINDOW THEMATIC REMOVAL (Full Context)
              Input: 0: yes, 1: i, 2: saw, 3: the, 4: new, 5: movie, 6: besides, 7: the, 8: part, 9: where, 10: the, 11: two, 12: gay, 13: men, 14: kissed, 15: what, 16: did, 17: you, 18: think, 19: of, 20: the, 21: movie, 22: overall
              Target: "gay"
              Output: {
                  "reasoning": "Removing the clause 'besides the part where the two gay men kissed' for a seamless transition between 'movie' and 'what'.",
                  "start_index": 6,
                  "end_index": 14
              }
        
            - EX 8: IDIOMATIC PHRASE IN LARGE WINDOW
              Input: 0: and, 1: was, 2: and, 3: then, 4: he, 5: said, 6: that, 7: anyway, 8: it, 9: was, 10: crazy, 11: just, 12: tell, 13: him, 14: to, 15: go, 16: to, 17: hell, 18: because, 19: I, 20: don't, 21: care, 22: about
              Target: "hell"
              Output: {
                  "reasoning": "Target 'hell' is part of the idiom 'go to hell'. Cutting the entire phrase (indices 11-17) for a natural transition.",
                  "start_index": 11,
                  "end_index": 17
              }
        
            CURRENT SEQUENCE:
            {indexed_text}
            TARGET WORD: "{target_word}"
        
            Return ONLY JSON in this format:
            {
            "reasoning": "Brief explanation of why these indices were chosen",
            "start_index": int,
            "end_index": int
            }
        """;

    /// <summary>
    /// Newline-normalized once, so the prompt is byte-identical whether this file
    /// was checked out with LF or CRLF endings, and with the trailing indentation
    /// the Python's closing triple-quote left behind restored.
    /// </summary>
    private static readonly string Template = TemplateSource.ReplaceLineEndings("\n") + "\n    ";

    /// <summary>Render the prompt for one hit.</summary>
    /// <param name="contextWindow">The words either side of the target, in order.</param>
    /// <param name="targetWord">The matched phrase, interpolated as TARGET WORD.</param>
    /// <param name="allowWidening">
    /// False for silence, bleep and every video edit, which appends
    /// <see cref="SurgicalRule"/>.
    /// </param>
    public static string Build(IReadOnlyList<Word> contextWindow, string targetWord, bool allowWidening)
    {
        ArgumentNullException.ThrowIfNull(contextWindow);

        return Template
            .Replace("{extra_rule}", allowWidening ? string.Empty : SurgicalRule, StringComparison.Ordinal)
            .Replace("{indexed_text}", RenderIndexedWindow(contextWindow), StringComparison.Ordinal)
            .Replace("{target_word}", targetWord ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// The context window as <c>index: word</c> lines numbered from zero - the
    /// index space the advisor answers in, and the one
    /// <see cref="SmartCutMapper"/> maps back to timestamps.
    /// </summary>
    /// <remarks>
    /// Word text goes in as the transcriber produced it, not normalized: the model
    /// is reading a sentence, and stripped punctuation reads worse.
    /// </remarks>
    public static string RenderIndexedWindow(IReadOnlyList<Word> contextWindow)
    {
        ArgumentNullException.ThrowIfNull(contextWindow);

        return string.Join("\n", contextWindow.Select((word, index) => $"{index}: {word.Text}"));
    }
}
