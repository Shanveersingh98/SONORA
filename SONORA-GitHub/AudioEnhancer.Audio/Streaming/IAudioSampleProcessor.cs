using AudioEnhancer.Core;
using AudioEnhancer.Dsp;

namespace AudioEnhancer.Audio;

/// <summary>
/// The single seam between the Windows audio backend and anything that wants to
/// change the audio. The backend knows only this; it has no idea the DSP engine exists.
///
/// <see cref="Process"/> runs on the real-time audio thread. An implementation must
/// not allocate, lock, log, block or touch the UI inside it, or the audio will glitch.
/// </summary>
public interface IAudioSampleProcessor
{
    /// <summary>
    /// Told the stream format before audio starts. Called off the audio thread,
    /// so allocating here is fine and expected.
    /// </summary>
    void Prepare(AudioStreamFormat format);

    /// <summary>Clears any internal tail. Called on stream start and after a dropout.</summary>
    void Reset();

    /// <summary>
    /// Processes interleaved samples in place, -1..+1, L,R,L,R,...
    /// Real-time thread: no allocations, no locks, no I/O.
    /// </summary>
    void Process(Span<float> interleavedSamples);

    /// <summary>
    /// Delay this processor adds, in frames. The stream needs it to keep the two
    /// ends of the pipeline aligned. It can change while running - the limiter
    /// reports zero while bypassed - so read it, do not cache it.
    /// </summary>
    int LatencyFrames { get; }
}

/// <summary>
/// Does nothing at all, on purpose. Useful as the default processor on a stream and
/// as the control case in tests: the audio that comes out must be bit-identical.
/// </summary>
public sealed class PassthroughSampleProcessor : IAudioSampleProcessor
{
    public static PassthroughSampleProcessor Instance { get; } = new();

    public void Prepare(AudioStreamFormat format) { }

    public void Reset() { }

    public void Process(Span<float> interleavedSamples) { }

    public int LatencyFrames => 0;
}

/// <summary>
/// Plugs the Part 2A DSP engine into the audio backend.
///
/// It translates a stream format into <c>Prepare</c> and forwards buffers to
/// <c>Process</c>. Since Part 2C, <see cref="WasapiAudioStream"/> calls it on its render
/// thread with the buffer that is about to be played.
/// </summary>
public sealed class DspEngineSampleProcessor : IAudioSampleProcessor
{
    private readonly IDspEngine _engine;
    private readonly DspEngine? _concrete;

    public DspEngineSampleProcessor(IDspEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));

        // LatencyFrames is not on IDspEngine, only on the concrete engine. When a test
        // double is passed in we simply report no added delay.
        _concrete = engine as DspEngine;
    }

    public DspEngineSampleProcessor() : this(new DspEngine())
    {
    }

    /// <summary>The engine being driven, so callers can inspect the chain.</summary>
    public IDspEngine Engine => _engine;

    /// <summary>The format last handed to <see cref="Prepare"/>, or null before that.</summary>
    public AudioStreamFormat? Format { get; private set; }

    /// <summary>True when audio passes straight through: the OFF state.</summary>
    public bool Bypass
    {
        get => _engine.Bypass;
        set => _engine.Bypass = value;
    }

    public int LatencyFrames => _concrete?.LatencyFrames ?? 0;

    private readonly Audio.Analysis.AudioSpectrumAnalyzer _spectrumAnalyzer = new();

    /// <summary>Read-only real-time FFT spectrum analyzer tap.</summary>
    public Audio.Analysis.AudioSpectrumAnalyzer SpectrumAnalyzer => _spectrumAnalyzer;

    public void Prepare(AudioStreamFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        Format = format;
        _engine.Prepare(format.SampleRate, format.Channels);
        _spectrumAnalyzer.Prepare(format.SampleRate);
        Reset();
    }

    public void Reset()
    {
        _concrete?.Reset();
        _spectrumAnalyzer.Reset();
    }

    /// <summary>Safe to call from the UI thread while audio is running.</summary>
    public void UpdateSettings(EnhancerSettings settings)
    {
        if (settings is null) return;
        _engine.ApplySettings(settings);

        // Step 7 diagnostic - see DiagnosticLog. Settings changes are UI-driven and rare
        // (a slider drag, not an audio-thread callback), so logging every one is never
        // spam and proves a slider move actually reached the DSP engine.
        DiagnosticLog.Write($"[DSP] Current settings: {settings}");
    }

    private volatile bool _muted;

    /// <summary>
    /// When true, renders digital silence (clears the buffer) in real time.
    /// Used for instant laptop hardware mute key (F3) handling.
    /// </summary>
    public bool Muted
    {
        get => _muted;
        set => _muted = value;
    }

    public void Process(Span<float> interleavedSamples)
    {
        if (_muted)
        {
            interleavedSamples.Clear();
            _spectrumAnalyzer.WriteRenderSamples(interleavedSamples, Format?.Channels ?? 2);
            return;
        }

        _engine.Process(interleavedSamples);
        _spectrumAnalyzer.WriteRenderSamples(interleavedSamples, Format?.Channels ?? 2);
    }
}
