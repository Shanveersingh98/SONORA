namespace AudioEnhancer.Dsp;

/// <summary>
/// Safe parameter ranges. Every setter clamps to these, so a bad binding, a corrupt
/// preset file or an out-of-range slider can never produce an unstable filter,
/// a NaN, or a dangerous output level.
/// </summary>
public static class DspLimits
{
    public const float GainMinDb = -24.0f;
    public const float GainMaxDb = 36.0f;

    public const float BandGainMinDb = -15.0f;
    public const float BandGainMaxDb = 15.0f;

    public const float AmountMin = 0.0f;
    public const float AmountMax = 1.0f;

    public const float CeilingMinDb = -12.0f;
    public const float CeilingMaxDb = 0.0f;

    /// <summary>Absolute output bound. Nothing ever leaves the engine outside this.</summary>
    public const float HardClipCeiling = 1.0f;

    public static float Clamp(float value, float min, float max)
    {
        if (float.IsNaN(value)) return min;   // NaN must never reach a filter
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    /// <summary>Maps a 0-100 slider onto a parameter range. Values outside 0-100 are clamped.</summary>
    public static float FromSlider(int slider, float atZero, float atHundred)
    {
        float t = Clamp(slider / 100.0f, 0.0f, 1.0f);
        return atZero + (atHundred - atZero) * t;
    }
}
