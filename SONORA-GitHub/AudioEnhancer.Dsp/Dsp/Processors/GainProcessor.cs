namespace AudioEnhancer.Dsp;

/// <summary>
/// Master volume boost, in dB, with per-sample smoothing so that dragging the
/// slider ramps instead of stepping. A raw jump in gain is an audible click.
/// </summary>
public sealed class GainProcessor : ProcessorBase, IGainProcessor
{
    private volatile float _targetGainDb;
    private float _currentGain = 1.0f;
    private float _smoothing;

    /// <summary>Ramp time. 20 ms is inaudible but still feels instant.</summary>
    public float SmoothingMs { get; set; } = 20.0f;

    /// <summary>Current smoothed gain as a linear factor. Exposed for tests and metering.</summary>
    public float CurrentLinearGain => _currentGain;

    public float GainDb => _targetGainDb;

    public void SetGainDb(float gainDb) =>
        _targetGainDb = DspLimits.Clamp(gainDb, DspLimits.GainMinDb, DspLimits.GainMaxDb);

    protected override void OnPrepare() =>
        _smoothing = DspMath.SmoothingCoefficient(SmoothingMs, SampleRate);

    /// <summary>Snaps the smoothed gain to its target, with no ramp.</summary>
    public override void Reset() => _currentGain = DspMath.DbToLinear(_targetGainDb);

    protected override void ProcessFrames(Span<float> buffer, int frameCount)
    {
        float target = DspMath.DbToLinear(_targetGainDb);
        float gain = _currentGain;

        // Fast path: already settled, so skip the per-sample smoother.
        if (MathF.Abs(gain - target) < 1e-7f)
        {
            _currentGain = target;
            if (MathF.Abs(target - 1.0f) < 1e-7f) return;   // unity: nothing to do at all

            for (int i = 0; i < buffer.Length; i++)
                buffer[i] *= target;
            return;
        }

        float smoothing = _smoothing;
        int channels = Channels;

        for (int frame = 0; frame < frameCount; frame++)
        {
            gain = target + (gain - target) * smoothing;
            int baseIndex = frame * channels;
            for (int ch = 0; ch < channels; ch++)
                buffer[baseIndex + ch] *= gain;
        }

        _currentGain = gain;
    }
}
