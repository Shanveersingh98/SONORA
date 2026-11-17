using AudioEnhancer.Core;

namespace AudioEnhancer.Dsp;

/// <summary>
/// The concrete signal chain:
///
///     Gain -> Bass -> Voice -> EQ (Clarity) -> Detail -> Limiter
///
/// That is the order documented on IDspEngine, with the Detail exciter inserted
/// before the limiter. The limiter stays last so no stage can push the output back
/// over the ceiling after it has been controlled.
///
/// No reference to WPF, to any audio API, or to Windows. The audio engine in a later
/// part owns an instance and calls Process on its real-time thread.
/// </summary>
public sealed class DspEngine : IDspEngine
{
    private readonly IDspProcessor[] _chain;
    private volatile bool _bypass;

    public DspEngine()
    {
        Gain = new GainProcessor();
        Bass = new BassProcessor();
        Voice = new VoiceProcessor();
        Eq = new EqProcessor();
        Detail = new DetailProcessor();
        Limiter = new Limiter();

        _chain = new IDspProcessor[] { Gain, Bass, Voice, Eq, Detail, Limiter };

        Prepare(48000, 2);
    }

    public GainProcessor Gain { get; }
    public BassProcessor Bass { get; }
    public VoiceProcessor Voice { get; }
    public EqProcessor Eq { get; }
    public DetailProcessor Detail { get; }
    public Limiter Limiter { get; }

    public int SampleRate { get; private set; } = 48000;
    public int Channels { get; private set; } = 2;

    /// <summary>
    /// Processing delay in frames, from the limiter's look-ahead. Zero while bypassed.
    /// The capture/render layer will need this to keep the two sides aligned.
    /// </summary>
    public int LatencyFrames => _bypass ? 0 : Limiter.LatencyFrames;

    /// <summary>When true, audio passes through untouched: the OFF state.</summary>
    public bool Bypass
    {
        get => _bypass;
        set => _bypass = value;
    }

    /// <summary>Allocates state for the stream format. Call off the audio thread.</summary>
    public void Prepare(int sampleRate, int channels)
    {
        SampleRate = sampleRate > 0 ? sampleRate : 48000;
        Channels = channels > 0 ? channels : 2;

        foreach (var stage in _chain)
            stage.Prepare(SampleRate, Channels);
    }

    /// <summary>Clears every filter tail. Call on stream start or after a dropout.</summary>
    public void Reset()
    {
        foreach (var stage in _chain)
            stage.Reset();
    }

    /// <summary>
    /// Turns the five 0-100 sliders into real DSP parameters. Safe to call from the UI
    /// thread at any time: every setter clamps and only writes a volatile field, and
    /// the audio thread picks the new values up on its next buffer.
    /// </summary>
    public void ApplySettings(EnhancerSettings settings)
    {
        if (settings is null) return;

        Gain.SetGainDb(SliderMap.VolumeDb(settings.Volume));
        Bass.SetAmount(SliderMap.Amount(settings.Bass));
        Voice.SetAmount(SliderMap.Amount(settings.Voice));

        Eq.SetBandGain(SliderMap.ClarityShelfBand, SliderMap.ClarityShelfDb(settings.Clarity));
        Eq.SetBandGain(SliderMap.ClarityAirBand, SliderMap.ClarityAirDb(settings.Clarity));

        Detail.SetAmount(SliderMap.Amount(settings.Detail));
    }

    /// <summary>
    /// Processes interleaved float samples in place. Real-time safe: no allocations,
    /// no locks, no file or UI access.
    /// </summary>
    public void Process(Span<float> interleavedSamples)
    {
        if (_bypass) return;   // true bypass: samples come out bit-identical

        var chain = _chain;
        for (int i = 0; i < chain.Length; i++)
            chain[i].Process(interleavedSamples);
    }
}
