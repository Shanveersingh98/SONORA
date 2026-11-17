namespace AudioEnhancer.Dsp;

/// <summary>Low-shelf bass enhancement: lifts everything below the corner frequency.</summary>
public sealed class BassProcessor : BiquadProcessorBase, IBassProcessor
{
    private volatile float _amount;

    public float Amount => _amount;
    public float FrequencyHz { get; set; } = SliderMap.BassFrequencyHz;

    public void SetAmount(float amount)
    {
        _amount = DspLimits.Clamp(amount, DspLimits.AmountMin, DspLimits.AmountMax);
        MarkDirty();
    }

    protected override bool IsTransparent => _amount < 0.0005f;

    protected override BiquadCoefficients Design(int sampleRate) =>
        BiquadCoefficients.LowShelf(FrequencyHz, SliderMap.BassGainDb(_amount), sampleRate);
}
