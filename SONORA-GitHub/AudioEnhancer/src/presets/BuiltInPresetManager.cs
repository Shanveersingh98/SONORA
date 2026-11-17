using AudioEnhancer.Core;

namespace AudioEnhancer.Presets;

/// <summary>
/// Pre-tuned consumer presets for music, gaming, movies, voice, bass boost, and maximum clean loudness.
/// </summary>
public sealed class BuiltInPresetManager : IPresetManager
{
    public Preset Music { get; } = new("Music", new EnhancerSettings(50, 50, 60, 30, 55));
    public Preset Gaming { get; } = new("Gaming", new EnhancerSettings(85, 55, 75, 65, 80));
    public Preset Movies { get; } = new("Movies", new EnhancerSettings(85, 80, 65, 75, 65));
    public Preset Voice { get; } = new("Voice", new EnhancerSettings(75, 10, 80, 90, 45));
    public Preset BassBoost { get; } = new("Bass Boost", new EnhancerSettings(80, 95, 50, 30, 45));
    public Preset Loud { get; } = new("Loud", new EnhancerSettings(85, 75, 70, 55, 60));
    public Preset Default { get; } = new("Balanced", new EnhancerSettings(75, 55, 65, 50, 60));
    public Preset Custom { get; } = new("Custom", new EnhancerSettings(75, 55, 65, 50, 60), IsCustom: true);

    /// <summary>
    /// Part 4 Step 6: One-tap Crystal Clear mode.
    /// </summary>
    public Preset CrystalClear { get; } = new("Crystal Clear", new EnhancerSettings(25, 15, 80, 55, 70));

    public IReadOnlyList<Preset> Presets { get; }

    public BuiltInPresetManager()
    {
        Presets = new[]
        {
            Default,
            Music,
            Gaming,
            Movies,
            Voice,
            BassBoost,
            Loud,
            Custom,
        };
    }
}
