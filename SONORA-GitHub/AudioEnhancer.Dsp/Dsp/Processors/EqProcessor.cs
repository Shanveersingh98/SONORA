namespace AudioEnhancer.Dsp;

/// <summary>
/// Two-band treble EQ, driven by the Clarity slider.
///   band 0 - high shelf at 5 kHz  (broad brightness)
///   band 1 - peaking bell at 12 kHz (air)
/// Bands run in series, one biquad per band per channel.
/// </summary>
public sealed class EqProcessor : ProcessorBase, IEqProcessor
{
    public const int BandCount = 2;

    private readonly float[] _bandGainDb = new float[BandCount];
    private BiquadCoefficients[] _coefficients = new BiquadCoefficients[BandCount];
    private BiquadState[] _states = Array.Empty<BiquadState>();   // [band * channels + channel]
    private volatile bool _dirty = true;

    public EqProcessor()
    {
        for (int band = 0; band < BandCount; band++)
            _coefficients[band] = BiquadCoefficients.Identity;
    }

    public float GetBandGain(int band) =>
        band >= 0 && band < BandCount ? _bandGainDb[band] : 0f;

    public void SetBandGain(int band, float gainDb)
    {
        if (band < 0 || band >= BandCount) return;   // out-of-range band is ignored, not a crash
        _bandGainDb[band] = DspLimits.Clamp(gainDb, DspLimits.BandGainMinDb, DspLimits.BandGainMaxDb);
        _dirty = true;
    }

    protected override void OnPrepare()
    {
        int required = BandCount * Channels;
        if (_states.Length != required)
            _states = new BiquadState[required];
        _dirty = true;
    }

    public override void Reset()
    {
        for (int i = 0; i < _states.Length; i++)
            _states[i].Reset();
    }

    private void Design()
    {
        _coefficients[SliderMap.ClarityShelfBand] = BiquadCoefficients.HighShelf(
            SliderMap.ClarityShelfHz, _bandGainDb[SliderMap.ClarityShelfBand], SampleRate);

        _coefficients[SliderMap.ClarityAirBand] = BiquadCoefficients.Peaking(
            SliderMap.ClarityAirHz, _bandGainDb[SliderMap.ClarityAirBand], SliderMap.ClarityAirQ, SampleRate);
    }

    protected override void ProcessFrames(Span<float> buffer, int frameCount)
    {
        if (_dirty)
        {
            Design();
            _dirty = false;
        }

        int channels = Channels;

        for (int band = 0; band < BandCount; band++)
        {
            if (MathF.Abs(_bandGainDb[band]) < 0.01f) continue;   // flat band: skip it entirely

            var coefficients = _coefficients[band];
            int stateOffset = band * channels;

            for (int frame = 0; frame < frameCount; frame++)
            {
                int baseIndex = frame * channels;
                for (int ch = 0; ch < channels; ch++)
                {
                    int i = baseIndex + ch;
                    buffer[i] = _states[stateOffset + ch].Process(buffer[i], in coefficients);
                }
            }
        }
    }
}
