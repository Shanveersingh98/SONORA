namespace AudioEnhancer.Dsp;

/// <summary>
/// Shared plumbing for every stage: format bookkeeping and the interleaved loop guard.
///
/// Threading model used throughout: the UI thread writes parameters into volatile
/// fields and raises a dirty flag; the audio thread recomputes coefficients at most
/// once per buffer, never per sample. No locks anywhere, so no priority inversion.
/// </summary>
public abstract class ProcessorBase : IDspProcessor
{
    protected int SampleRate { get; private set; } = 48000;
    protected int Channels { get; private set; } = 2;
    protected bool IsPrepared { get; private set; }

    public virtual void Prepare(int sampleRate, int channels)
    {
        SampleRate = sampleRate > 0 ? sampleRate : 48000;
        Channels = channels > 0 ? channels : 2;
        IsPrepared = true;
        OnPrepare();
        Reset();
    }

    /// <summary>Allocate or resize state here. Never allocate in Process.</summary>
    protected virtual void OnPrepare() { }

    public abstract void Reset();

    public void Process(Span<float> interleavedSamples)
    {
        if (!IsPrepared) return;

        int frameCount = interleavedSamples.Length / Channels;
        if (frameCount <= 0) return;

        // Ignore any ragged tail so a stage can never read past a partial frame.
        ProcessFrames(interleavedSamples[..(frameCount * Channels)], frameCount);
    }

    protected abstract void ProcessFrames(Span<float> buffer, int frameCount);
}

/// <summary>A stage built around one biquad per channel.</summary>
public abstract class BiquadProcessorBase : ProcessorBase
{
    private BiquadState[] _states = Array.Empty<BiquadState>();
    private BiquadCoefficients _coefficients = BiquadCoefficients.Identity;
    private volatile bool _dirty = true;

    protected void MarkDirty() => _dirty = true;

    protected abstract BiquadCoefficients Design(int sampleRate);

    /// <summary>True when the current settings are a no-op, so the stage can be skipped.</summary>
    protected virtual bool IsTransparent => false;

    protected override void OnPrepare()
    {
        if (_states.Length != Channels)
            _states = new BiquadState[Channels];
        _dirty = true;
    }

    public override void Reset()
    {
        for (int i = 0; i < _states.Length; i++)
            _states[i].Reset();
    }

    protected override void ProcessFrames(Span<float> buffer, int frameCount)
    {
        if (_dirty)
        {
            _coefficients = Design(SampleRate);
            _dirty = false;
        }

        if (IsTransparent) return;

        var coefficients = _coefficients;
        var states = _states;
        int channels = Channels;

        for (int frame = 0; frame < frameCount; frame++)
        {
            int baseIndex = frame * channels;
            for (int ch = 0; ch < channels; ch++)
            {
                int i = baseIndex + ch;
                buffer[i] = states[ch].Process(buffer[i], in coefficients);
            }
        }
    }
}
