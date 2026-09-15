namespace FoulFilterNet.Domain;

/// <summary>How a <see cref="Hit"/> is rendered inaudible.</summary>
public enum CensorMethod
{
    /// <summary>Zero the volume over the hit window.</summary>
    Silence,

    /// <summary>Mute the hit window and mix a 1 kHz tone over it.</summary>
    Bleep,

    /// <summary>Cut the audio out entirely. Audio files only; video falls back to <see cref="Silence"/>.</summary>
    Remove,
}

/// <summary>What kind of media a file holds.</summary>
public enum MediaKind
{
    /// <summary>Not a media file this application can process.</summary>
    Unknown,

    /// <summary>Audio only.</summary>
    Audio,

    /// <summary>Carries a video stream; frames must survive the edit untouched.</summary>
    Video,
}
