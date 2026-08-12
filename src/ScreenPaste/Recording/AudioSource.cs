namespace ScreenPaste.Recording;

/// <summary>Which audio to mux into a recording.</summary>
public enum AudioSource
{
    None,          // silent
    System,        // WASAPI loopback of the default render device
    Microphone,    // default capture device
    Both,          // system + microphone, mixed
}

public static class AudioSources
{
    public static AudioSource Parse(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "system" => AudioSource.System,
        "mic" or "microphone" => AudioSource.Microphone,
        "both" => AudioSource.Both,
        _ => AudioSource.None,
    };

    public static string ToToken(this AudioSource s) => s switch
    {
        AudioSource.System => "system",
        AudioSource.Microphone => "mic",
        AudioSource.Both => "both",
        _ => "none",
    };

    public static bool WantsSystem(this AudioSource s) => s is AudioSource.System or AudioSource.Both;
    public static bool WantsMic(this AudioSource s) => s is AudioSource.Microphone or AudioSource.Both;
}
