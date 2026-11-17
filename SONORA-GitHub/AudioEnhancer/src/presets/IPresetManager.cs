using AudioEnhancer.Core;

namespace AudioEnhancer.Presets;

/// <summary>A named set of slider values. "Custom" is a special entry meaning "the user moved a slider".</summary>
public sealed record Preset(string Name, EnhancerSettings Settings, bool IsCustom = false)
{
    public string Icon => Name switch
    {
        "Music" => "🎵",
        "Gaming" => "🎮",
        "Movies" => "🎬",
        "Voice" => "🎙️",
        "Bass Boost" => "⚡",
        "Loud" => "🔊",
        "Balanced" => "⚖️",
        "Custom" => "🎛️",
        _ => "✨"
    };

    public string Subtitle => Name switch
    {
        "Music" => "Warm & Wide",
        "Gaming" => "Spatial 3D",
        "Movies" => "Cinema Bass",
        "Voice" => "Speech Focus",
        "Bass Boost" => "Sub Punch",
        "Loud" => "Max Power",
        "Balanced" => "Studio Flat",
        "Custom" => "Personalized",
        _ => "Enhanced"
    };

    public string AccentColor => Name switch
    {
        "Music" => "#B026FF",
        "Gaming" => "#00F5A0",
        "Movies" => "#FF9F43",
        "Voice" => "#00D2D3",
        "Bass Boost" => "#FF2A6D",
        "Loud" => "#FFB300",
        "Balanced" => "#48DBFB",
        "Custom" => "#E056FD",
        _ => "#4CC2FF"
    };

    public string IconGeometry => Name switch
    {
        "Music" => "M12 3v10.55c-.59-.34-1.27-.55-2-.55-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4V7h4V3h-6z",
        "Gaming" => "M21 6H3c-1.1 0-2 .9-2 2v8c0 1.1.9 2 2 2h18c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2zm-10 7H8v3H6v-3H3v-2h3V8h2v3h3v2zm4.5 2c-.83 0-1.5-.67-1.5-1.5s.67-1.5 1.5-1.5 1.5.67 1.5 1.5-.67 1.5-1.5 1.5zm3-3c-.83 0-1.5-.67-1.5-1.5S17.67 9 18.5 9s1.5.67 1.5 1.5-.67 1.5-1.5 1.5z",
        "Movies" => "M18 4l2 4h-3l-2-4h-2l2 4h-3l-2-4H8l2 4H7L5 4H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V4h-4z",
        "Voice" => "M12 14c1.66 0 3-1.34 3-3V5c0-1.66-1.34-3-3-3S9 3.34 9 5v6c0 1.66 1.34 3 3 3zm5.3-3c0 3-2.54 5.1-5.3 5.1S6.7 14 6.7 11H5c0 3.41 2.72 6.23 6 6.72V21h2v-3.28c3.28-.48 6-3.3 6-6.72h-1.7z",
        "Bass Boost" => "M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z",
        "Loud" => "M3 9v6h4l5 5V4L7 9H3zm14.5 3c0-2.5-1.4-4.68-3.5-5.8v11.6c2.1-1.12 3.5-3.3 3.5-5.8z",
        "Balanced" => "M10 18h4v-2h-4v2zM3 6v2h18V6H3zm3 7h12v-2H6v2z",
        "Custom" => "M3 17v2h6v-2H3zM3 5v2h10V5H3zm10 16v-2h8v-2h-8v-2h-2v6h2zM7 9v2H3v2h4v2h2V9H7zm14 4v-2H11v2h10zm-6-4h2V7h4V5h-4V3h-2v6z",
        _ => "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 14.5v-9l6 4.5-6 4.5z"
    };
}

public interface IPresetManager
{
    IReadOnlyList<Preset> Presets { get; }
    Preset Default { get; }
    Preset Custom { get; }
    Preset Music { get; }
    Preset Gaming { get; }
    Preset Movies { get; }
    Preset Voice { get; }
    Preset BassBoost { get; }
    Preset Loud { get; }

    /// <summary>
    /// Part 4 Step 6: the one-tap "Crystal Clear" bundle.
    /// </summary>
    Preset CrystalClear { get; }
}
