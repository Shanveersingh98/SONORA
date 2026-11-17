namespace AudioEnhancer.Dsp;

/// <summary>
/// Speech emphasis: a peaking bell in the presence band, around 2.6 kHz, where
/// consonants and intelligibility live.
/// </summary>
public sealed class VoiceProcessor : BiquadProcessorBase, IVoiceProcessor
{
    private volatile float _amount;

    public float Amount => _amount;
    public float FrequencyHz { get; set; } = SliderMap.VoiceFrequencyHz;
    public float Q { get; set; } = SliderMap.VoiceQ;

    public void SetAmount(float amount)
    {
        _amount = DspLimits.Clamp(amount, DspLimits.AmountMin, DspLimits.AmountMax);
        MarkDirty();
    }

    protected override bool IsTransparent => _amount < 0.0005f;

    protected override BiquadCoefficients Design(int sampleRate) =>
        BiquadCoefficients.Peaking(FrequencyHz, SliderMap.VoiceGainDb(_amount), Q, sampleRate);
}
