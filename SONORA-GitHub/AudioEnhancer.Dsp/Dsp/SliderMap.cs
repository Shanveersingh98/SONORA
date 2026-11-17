namespace AudioEnhancer.Dsp;

/// <summary>
/// The single place where the five 0-100 sliders become real DSP quantities.
/// Keeping it here means the UI never has to know about decibels, and the tuning
/// can be adjusted in one file.
///
/// Every mapping starts at "no effect" when the slider is 0, matching the contract
/// documented on EnhancerSettings.
/// </summary>
public static class SliderMap
{
    /// <summary>Volume / Loudness: 0 = unity (0 dB), 100 = +36 dB progressive loudness.</summary>
    public static float VolumeDb(int slider) => DspLimits.FromSlider(slider, 0f, 36f);

    /// <summary>Bass / Voice / Detail sliders map straight onto a 0..1 amount.</summary>
    public static float Amount(int slider) => DspLimits.FromSlider(slider, 0f, 1f);

    /// <summary>Bass amount to low-shelf gain: 0 = flat, 1 = +15 dB.</summary>
    public static float BassGainDb(float amount) => DspLimits.Clamp(amount, 0f, 1f) * 15f;
    public const float BassFrequencyHz = 85f;

    /// <summary>Voice amount to presence-bell gain: 0 = flat, 1 = +11 dB.</summary>
    public static float VoiceGainDb(float amount) => DspLimits.Clamp(amount, 0f, 1f) * 11f;
    public const float VoiceFrequencyHz = 2600f;
    public const float VoiceQ = 0.85f;

    /// <summary>
    /// Clarity drives the two EQ bands together: a broad treble shelf plus a smaller
    /// lift right at the top, which reads as "open" rather than just "bright".
    /// </summary>
    public static float ClarityShelfDb(int slider) => DspLimits.FromSlider(slider, 0f, 11f);
    public static float ClarityAirDb(int slider) => DspLimits.FromSlider(slider, 0f, 6.5f);

    public const int ClarityShelfBand = 0;
    public const int ClarityAirBand = 1;
    public const float ClarityShelfHz = 4800f;
    public const float ClarityAirHz = 12000f;
    public const float ClarityAirQ = 0.7f;
}
