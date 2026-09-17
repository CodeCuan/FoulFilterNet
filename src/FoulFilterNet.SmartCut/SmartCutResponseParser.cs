using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FoulFilterNet.Domain;

namespace FoulFilterNet.SmartCut;

/// <summary>
/// Turns whatever an LLM said into a <see cref="SmartCutDecision"/>. Ported from
/// the tail of <c>get_ai_smart_cut</c> in <c>Legacy/src/ai_helper.py</c>.
/// </summary>
/// <remarks>
/// The three-way split is the whole point. A model that says NONE has judged the
/// hit a false positive and the hit is dropped; a model that could not be reached,
/// refused, or answered gibberish leaves the hit exactly as the matcher found it.
/// Folding the second case into the first would turn one LLM outage into a file
/// that is silently not censored at all.
/// </remarks>
public static partial class SmartCutResponseParser
{
    /// <summary>
    /// The Python's <c>re.search(r"\{[\s\S]*?\}", ...)</c>: the first braced run in
    /// the response, so JSON survives a preamble, a sign-off or a code fence.
    /// Non-greedy, which is safe because the response schema is flat.
    /// </summary>
    [GeneratedRegex(@"\{[\s\S]*?\}")]
    private static partial Regex FirstJsonObject();

    /// <summary>
    /// Parse one model response and map it onto <paramref name="contextWindow"/>.
    /// Never throws for a bad response; only a null window is a programming error.
    /// </summary>
    public static SmartCutDecision Parse(
        string? responseText,
        IReadOnlyList<Word> contextWindow,
        int centerIndex,
        bool allowWidening
    )
    {
        ArgumentNullException.ThrowIfNull(contextWindow);

        // Checked first, and against the whole response rather than a parsed
        // field, because a model that talks itself out of the edit in prose is
        // still saying "do not cut this".
        if (
            !string.IsNullOrEmpty(responseText)
            && responseText.Contains("NONE", StringComparison.OrdinalIgnoreCase)
        )
        {
            return SmartCutDecision.Reject;
        }

        if (
            string.IsNullOrEmpty(responseText)
            || string.Equals(
                responseText,
                SmartCutResponses.ApiUnavailable,
                StringComparison.Ordinal
            )
            || string.Equals(
                responseText,
                SmartCutResponses.ErrorOrRefusal,
                StringComparison.Ordinal
            )
        )
        {
            return SmartCutDecision.KeepOriginal;
        }

        try
        {
            var json = ExtractFirstJsonObject(responseText);
            if (json is null)
            {
                return SmartCutDecision.KeepOriginal;
            }

            using var document = JsonDocument.Parse(json);

            return SmartCutMapper.Map(
                ReadIndex(document.RootElement, "start_index"),
                ReadIndex(document.RootElement, "end_index"),
                contextWindow,
                centerIndex,
                allowWidening
            );
        }
        catch (Exception ex)
            when (ex
                    is JsonException
                        or FormatException
                        or OverflowException
                        or InvalidOperationException
                        or ArgumentException
            )
        {
            // Matches the Python's blanket except: a malformed answer is worth no
            // more than a missing one, and neither may fail a job.
            return SmartCutDecision.KeepOriginal;
        }
    }

    /// <summary>
    /// The first <c>{...}</c> run in <paramref name="responseText"/>, or null when
    /// there is none.
    /// </summary>
    public static string? ExtractFirstJsonObject(string? responseText)
    {
        if (string.IsNullOrEmpty(responseText))
        {
            return null;
        }

        var match = FirstJsonObject().Match(responseText);

        return match.Success ? match.Value : null;
    }

    /// <summary>
    /// Read one index, accepting the shapes small models actually emit: a number,
    /// a whole float, or a quoted number. Mirrors the Python's <c>int(...)</c>.
    /// </summary>
    private static int ReadIndex(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            throw new JsonException($"Smart Cut response is missing '{name}'.");
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out var index)
                ? index
                : (int)value.GetDouble(),
            JsonValueKind.String => int.Parse(
                value.GetString() ?? string.Empty,
                CultureInfo.InvariantCulture
            ),
            _ => throw new JsonException($"Smart Cut response has a non-numeric '{name}'."),
        };
    }
}
