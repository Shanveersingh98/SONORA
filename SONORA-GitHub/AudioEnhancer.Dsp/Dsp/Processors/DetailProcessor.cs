namespace AudioEnhancer.Dsp;

/// <summary>
/// Detail enhancement: a harmonic exciter.
///
/// The high band is split off with a one-pole filter, soft-saturated to create
/// harmonics that were never in the source, then mixed back at a low level. That is
/// why this is not an EQ band: a shelf can only amplify treble that already exists,
/// whereas this manufactures new top end, which is what makes dull material read as
/// detailed.
/// </summary>
public sealed class DetailProcessor : ProcessorBase, IDetailProcessor
{
    private float[] _lowpassState = Array.Empty<float>();
    private float _lowpassCoefficient;
    private volatile float _amount;
    private volatile bool _dirty = true;

    /// <summary>How hard the high band is driven into saturation before mixing.</summary>
    private const float Drive = 4.2f;

    /// <summary>Ceiling on how much excited signal gets blended back in.</summary>
    private const float MaxMix = 0.40f;

    public float Amount => _amount;

    private float _frequencyHz = 5000f;
    public float FrequencyHz
    {
        get => _frequencyHz;
        set { _frequencyHz = DspLimits.Clamp(value, 2000f, 14000f); _dirty = true; }
    }

    public void SetAmount(float amount) =>
        _amount = DspLimits.Clamp(amount, DspLimits.AmountMin, DspLimits.AmountMax);

    protected override void OnPrepare()
    {
        if (_lowpassState.Length != Channels)
            _lowpassState = new float[Channels];
        _dirty = true;
    }

    public override void Reset() => Array.Clear(_lowpassState, 0, _lowpassState.Length);

    protected override void ProcessFrames(Span<float> buffer, int frameCount)
    {
        if (_dirty)
        {
            // One-pole lowpass at the split point; the high band is x minus its lowpass.
            float w = 2.0f * MathF.PI * _frequencyHz / SampleRate;
            _lowpassCoefficient = DspLimits.Clamp(MathF.Exp(-w), 0.0f, 0.9999f);
            _dirty = false;
        }

        float amount = _amount;
        if (amount < 0.001f) return;

        float a = _lowpassCoefficient;
        float oneMinusA = 1.0f - a;
        float mix = amount * MaxMix;
        var state = _lowpassState;
        int channels = Channels;

        for (int frame = 0; frame < frameCount; frame++)
        {
            int baseIndex = frame * channels;
            for (int ch = 0; ch < channels; ch++)
            {
                int i = baseIndex + ch;
                float x = buffer[i];

                float low = oneMinusA * x + a * state[ch];
                state[ch] = DspMath.FlushDenormal(low);

                float high = x - low;
                buffer[i] = x + DspMath.SoftSaturate(high * Drive) * mix;
            }
        }
    }
}
