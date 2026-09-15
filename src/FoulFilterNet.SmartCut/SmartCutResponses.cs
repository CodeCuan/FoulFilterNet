namespace FoulFilterNet.SmartCut;

/// <summary>
/// The sentinel strings a transport returns in place of a model answer. Carried
/// over from <c>Legacy/src/ai_helper.py</c>, where they travel through the same
/// <c>str</c> channel as a real response.
/// </summary>
/// <remarks>
/// Both mean "keep the original timestamps", never "reject the hit" - see
/// <see cref="SmartCutResponseParser"/>.
/// </remarks>
public static class SmartCutResponses
{
    /// <summary>The transport could not reach, or was not configured for, a model.</summary>
    public const string ApiUnavailable = "API_UNAVAILABLE";

    /// <summary>The model was reached but returned no usable content, typically a safety refusal.</summary>
    public const string ErrorOrRefusal = "ERROR_OR_REFUSAL";
}
