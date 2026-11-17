namespace AudioEnhancer.Dsp;

/// <summary>
/// Look-ahead limiter plus absolute clipping protection. Always the last stage.
///
/// Per frame:
///   1. Peak across all channels of the incoming frame feeds a peak-hold detector
///      (instant attack, slow decay), so the envelope tracks the loudest point of the
///      waveform rather than the current sample value.
///   2. Work out the gain that puts that envelope on the ceiling.
///   3. Smooth it: fast attack, slow release, so it bends rather than steps.
///   4. Apply it to a frame taken from a short delay line.
///
/// Step 4 is the look-ahead. The audio is held back a few milliseconds so the gain
/// has finished ducking before the loud sample reaches the output; without it the
/// envelope lags the transient and the signal overshoots the ceiling. The delay is
/// five attack time constants, enough for the smoother to converge past 99%.
///
/// The final hard clamp is the guarantee: nothing leaves the engine beyond +/-1.0,
/// whatever the settings.
/// </summary>
public sealed class Limiter : ProcessorBase, ILimiter
{
    private float[] _delay = Array.Empty<float>();
    private int _delayFrames = 1;
    private int _delayIndex;

    private float _detectorEnvelope;
    private float _envelopeGain = 1.0f;
    private float _attackCoefficient;
    private float _releaseCoefficient;
    private float _maxGainReduction = 1.0f;

    private volatile float _ceilingDb = -0.3f;
    private volatile float _attackMs = 1.0f;
    private volatile float _releaseMs = 120.0f;
    private volatile bool _clippingProtection = true;
    private volatile bool _timingDirty = true;

    private const int LookaheadTimeConstants = 5;

    public float CeilingDb => _ceilingDb;

    public void SetCeilingDb(float ceilingDb) =>
        _ceilingDb = DspLimits.Clamp(ceilingDb, DspLimits.CeilingMinDb, DspLimits.CeilingMaxDb);

    /// <summary>
    /// Attack time constant. This also sizes the look-ahead, so the delay line is only
    /// resized on Prepare; changing it mid-stream adjusts smoothing but keeps latency.
    /// </summary>
    public float AttackMs
    {
        get => _attackMs;
        set { _attackMs = DspLimits.Clamp(value, 0.1f, 10.0f); _timingDirty = true; }
    }

    public float ReleaseMs
    {
        get => _releaseMs;
        set { _releaseMs = DspLimits.Clamp(value, 10.0f, 1000.0f); _timingDirty = true; }
    }

    /// <summary>Final hard clamp to +/-1.0. Leave this on; it is the last line of defence.</summary>
    public bool ClippingProtection
    {
        get => _clippingProtection;
        set => _clippingProtection = value;
    }

    /// <summary>Look-ahead latency in frames.</summary>
    public int LatencyFrames => _delayFrames;

    /// <summary>Deepest gain reduction since the last read, linear (1 = none). For a future meter.</summary>
    public float ReadMaxGainReduction()
    {
        float value = _maxGainReduction;
        _maxGainReduction = 1.0f;
        return value;
    }

    protected override void OnPrepare()
    {
        float lookaheadMs = _attackMs * LookaheadTimeConstants;
        _delayFrames = Math.Max(1, (int)MathF.Round(lookaheadMs * 0.001f * SampleRate));

        int required = _delayFrames * Channels;
        if (_delay.Length != required)
            _delay = new float[required];

        _timingDirty = true;
    }

    public override void Reset()
    {
        Array.Clear(_delay, 0, _delay.Length);
        _delayIndex = 0;
        _detectorEnvelope = 0.0f;
        _envelopeGain = 1.0f;
        _maxGainReduction = 1.0f;
    }

    protected override void ProcessFrames(Span<float> buffer, int frameCount)
    {
        if (_timingDirty)
        {
            // Never let the attack outrun the look-ahead actually allocated.
            float maxAttackMs = _delayFrames * 1000.0f / SampleRate / LookaheadTimeConstants;
            _attackCoefficient = DspMath.SmoothingCoefficient(MathF.Min(_attackMs, maxAttackMs), SampleRate);
            _releaseCoefficient = DspMath.SmoothingCoefficient(_releaseMs, SampleRate);
            _timingDirty = false;
        }

        float ceiling = DspMath.DbToLinear(_ceilingDb);
        float kneeThreshold = DspMath.DbToLinear(_ceilingDb - 2.5f);
        bool clip = _clippingProtection;      // hoisted: no volatile reads inside the loop
        float attack = _attackCoefficient;
        float release = _releaseCoefficient;
        float gain = _envelopeGain;
        float envelope = _detectorEnvelope;
        float minGain = _maxGainReduction;
        var delay = _delay;
        int delayIndex = _delayIndex;
        int delayFrames = _delayFrames;
        int channels = Channels;

        for (int frame = 0; frame < frameCount; frame++)
        {
            int baseIndex = frame * channels;
            int delayBase = delayIndex * channels;

            // Channel-linked detection keeps the stereo image stable.
            float peak = 0.0f;
            for (int ch = 0; ch < channels; ch++)
            {
                float magnitude = MathF.Abs(buffer[baseIndex + ch]);
                if (magnitude > peak) peak = magnitude;
            }

            // Peak hold: jump straight to a new peak, decay away from it slowly. Following
            // |x| directly would let the gain drift back up between samples, and the output
            // would sit above the ceiling.
            envelope = peak > envelope ? peak : DspMath.FlushDenormal(envelope * release);

            // Soft-knee dynamic smoothing: prevents harsh pumping while allowing maximum clean loudness
            float targetGain;
            if (envelope <= kneeThreshold)
            {
                targetGain = 1.0f;
            }
            else if (envelope >= ceiling)
            {
                targetGain = ceiling / envelope;
            }
            else
            {
                float t = (envelope - kneeThreshold) / (ceiling - kneeThreshold);
                float smoothT = t * t * (3.0f - 2.0f * t);
                float fullReduction = ceiling / envelope;
                targetGain = 1.0f + (fullReduction - 1.0f) * smoothT;
            }

            gain = targetGain + (gain - targetGain) * (targetGain < gain ? attack : release);
            gain = DspMath.FlushDenormal(gain);
            if (gain < minGain) minGain = gain;

            for (int ch = 0; ch < channels; ch++)
            {
                int i = baseIndex + ch;
                int d = delayBase + ch;

                float delayed = delay[d];   // read the older sample out...
                delay[d] = buffer[i];       // ...then park the new one in its slot

                float y = delayed * gain;
                if (clip)
                {
                    float limit = MathF.Min(ceiling, DspLimits.HardClipCeiling);
                    if (y > limit) y = limit;
                    else if (y < -limit) y = -limit;
                }
                buffer[i] = y;
            }

            delayIndex++;
            if (delayIndex >= delayFrames) delayIndex = 0;
        }

        _delayIndex = delayIndex;
        _detectorEnvelope = envelope;
        _envelopeGain = gain;
        _maxGainReduction = minGain;
    }
}
