namespace AudioEnhancer.Dsp;

/// <summary>One building block of the signal chain. All processors work in place on a buffer.</summary>
public interface IDspProcessor
{
    void Prepare(int sampleRate, int channels);
    void Process(Span<float> interleavedSamples);
    void Reset();
}

/// <summary>Volume boost.</summary>
public interface IGainProcessor : IDspProcessor { void SetGainDb(float gainDb); }

/// <summary>Multi-band equalizer. Also used for Clarity and Detail (treble bands).</summary>
public interface IEqProcessor : IDspProcessor { void SetBandGain(int band, float gainDb); }

/// <summary>Low-frequency enhancement. Amount is 0..1.</summary>
public interface IBassProcessor : IDspProcessor { void SetAmount(float amount); }

/// <summary>Speech-frequency emphasis. Amount is 0..1.</summary>
public interface IVoiceProcessor : IDspProcessor { void SetAmount(float amount); }

/// <summary>
/// High-frequency detail enhancement. Amount is 0..1.
/// Added in Part 2A: the Detail slider is not a plain treble lift, it generates new
/// harmonics, so it cannot be expressed through IEqProcessor.SetBandGain.
/// </summary>
public interface IDetailProcessor : IDspProcessor { void SetAmount(float amount); }

/// <summary>Last stage: keeps peaks under the ceiling so boosted audio never clips.</summary>
public interface ILimiter : IDspProcessor { void SetCeilingDb(float ceilingDb); }
