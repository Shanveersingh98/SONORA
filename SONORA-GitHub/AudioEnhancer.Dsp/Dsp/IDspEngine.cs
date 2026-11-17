using AudioEnhancer.Core;

namespace AudioEnhancer.Dsp;

/// <summary>
/// The whole signal chain in one object: Gain -> Bass -> Voice -> EQ -> Limiter.
/// The audio engine hands it a buffer of samples; it returns them enhanced.
/// Part 2 implements it by chaining the processors from IDspProcessors.cs.
/// </summary>
public interface IDspEngine
{
    void Prepare(int sampleRate, int channels);

    /// <summary>Converts the five 0-100 sliders into settings for each processor.</summary>
    void ApplySettings(EnhancerSettings settings);

    /// <summary>When true, audio passes through untouched (the OFF state).</summary>
    bool Bypass { get; set; }

    /// <summary>
    /// Processes interleaved float samples (L,R,L,R,...) in place.
    /// Runs on the real-time audio thread: no allocations, no locks, no file/UI access.
    /// </summary>
    void Process(Span<float> interleavedSamples);
}
