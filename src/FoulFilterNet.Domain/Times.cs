using System.Globalization;

namespace FoulFilterNet.Domain;

/// <summary>
/// Time arithmetic and rendering shared by every stage.
/// </summary>
/// <remarks>
/// The formatting members exist because FFmpeg filtergraphs are compared
/// byte-for-byte against the Python implementation. Python renders floats with
/// <c>repr</c>, which prints <c>1.0</c>; C#'s default <see cref="double.ToString()"/>
/// prints <c>1</c>. FFmpeg accepts both, so a mismatch is silent at runtime and
/// only a string comparison catches it. Use these rather than ToString.
/// </remarks>
public static class Times
{
    /// <summary>Decimal places every timestamp is rounded to before it is stored or rendered.</summary>
    public const int Precision = 3;

    /// <summary>Round to <see cref="Precision"/> decimal places, matching Python's <c>round(x, 3)</c>.</summary>
    public static double Round(double seconds) =>
        Math.Round(seconds, Precision, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Render as Python's <c>repr</c> does: shortest round-trippable form, but
    /// always carrying a decimal point. Used by the silence filter, which
    /// interpolates raw floats.
    /// </summary>
    public static string ToRepr(double seconds)
    {
        var text = seconds.ToString("R", CultureInfo.InvariantCulture);
        var needsPoint = text.IndexOf('.', StringComparison.Ordinal) < 0
                      && text.IndexOf('E', StringComparison.Ordinal) < 0;
        return needsPoint ? text + ".0" : text;
    }

    /// <summary>
    /// Render with exactly three decimal places, matching Python's <c>{:.3f}</c>.
    /// Used by the bleep and remove filters.
    /// </summary>
    public static string ToFixed(double seconds) =>
        seconds.ToString("F3", CultureInfo.InvariantCulture);
}
