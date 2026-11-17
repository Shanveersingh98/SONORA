namespace AudioEnhancer.Audio;

/// <summary>
/// Pulls interleaved frames out of an <see cref="AudioRingBuffer"/> at one sample rate
/// and delivers them at another, continuously, one render callback at a time.
///
/// <para>Two distinct jobs share this one class, because they share one number - the
/// ratio of source frames to destination frames:</para>
/// <list type="bullet">
/// <item>Format conversion, when the capture and render endpoints simply run at
/// different nominal rates (44.1 kHz system mix onto a 48 kHz output device, say).
/// This sets the <em>nominal</em> ratio, fixed for the life of the stream.</item>
/// <item>Clock-drift correction, where <see cref="RingFillController"/> nudges the
/// ratio a few hundred parts-per-million away from nominal so the ring buffer's fill
/// level tracks a target instead of slowly draining or filling as two independent
/// hardware clocks disagree about how long a second is.</item>
/// </list>
///
/// <para><b>Ratio 1.0 is exact, not approximate.</b> When the ratio is close enough to
/// unity that there is nothing to correct, <see cref="Render"/> calls
/// <see cref="AudioRingBuffer.Read"/> directly instead of running interpolation math
/// over it. That is not an optimisation so much as a correctness requirement: floating
/// point interpolation of a signal against itself is not guaranteed bit-identical, and
/// unchanged audio should never come out of this stage looking different from how it
/// went in. <see cref="DeadbandEpsilon"/> is the width of that "exactly 1.0" band.</para>
///
/// <para><b>Streaming, not block-based.</b> A caller asks for however many destination
/// frames it needs this callback - always <see cref="AudioStreamFormat"/>'s render
/// buffer size in the real stream, but the class does not assume that. Interpolation
/// state (the last two source frames and the fractional position between them) is kept
/// across calls, so a resample that started mid-frame on one callback finishes
/// correctly on the next. This is what "continuously" means for a ring buffer that is
/// fed and drained on two different threads with no shared block boundary.</para>
///
/// <para>Allocation-free after <see cref="Prepare"/>: every array is sized once, off the
/// audio thread, and <see cref="Render"/> never grows or replaces them. Pulling one
/// frame at a time from the ring costs a couple of volatile reads and a short copy, not
/// a heap allocation, so it is safe to call from the render thread.</para>
/// </summary>
public sealed class AdaptiveResampler
{
    /// <summary>
    /// How close <see cref="Ratio"/> must be to 1.0 before <see cref="Render"/> treats it
    /// as exactly unity and skips interpolation entirely. Real corrections from
    /// <see cref="RingFillController"/> run at least three orders of magnitude wider than
    /// this, so the deadband only ever catches a genuinely unadjusted ratio.
    /// </summary>
    public const double DeadbandEpsilon = 1e-7;

    /// <summary>Defensive floor and ceiling on <see cref="Ratio"/>, far outside anything a real drift correction would ask for.</summary>
    private const double MinRatio = 0.1;
    private const double MaxRatio = 10.0;

    private int _channels;
    private double _ratio = 1.0;
    private double _nominalRatio = 1.0;

    private float[] _previousFrame = Array.Empty<float>();
    private float[] _nextFrame = Array.Empty<float>();
    private double _phase;
    private bool _primed;

    /// <summary>The channel count fixed by the last <see cref="Prepare"/> call.</summary>
    public int Channels => _channels;

    /// <summary>
    /// The conversion this resampler was built for: capture sample rate divided by
    /// render sample rate. <see cref="Ratio"/> starts here and is nudged away from it
    /// by <see cref="RingFillController"/>, never replaced outright.
    /// </summary>
    public double NominalRatio => _nominalRatio;

    /// <summary>
    /// Source frames consumed per destination frame produced, right now. Above 1.0 means
    /// downsampling (input runs out faster than output is produced); below 1.0 means
    /// upsampling. Setting it is safe from the thread that owns <see cref="Render"/> calls
    /// - there is no synchronisation, by design, because exactly one thread ever touches
    /// either member in the real stream.
    /// </summary>
    public double Ratio
    {
        get => _ratio;
        set => _ratio = double.IsFinite(value) ? Math.Clamp(value, MinRatio, MaxRatio) : _ratio;
    }

    /// <summary>True when <see cref="Ratio"/> is close enough to 1.0 to skip interpolation.</summary>
    public bool IsUnity => Math.Abs(_ratio - 1.0) < DeadbandEpsilon;

    /// <summary>
    /// Sizes the resampler for a channel count and a source/target sample rate pair.
    /// Allocates its (small, fixed) working arrays - call this off the audio thread,
    /// once per opened session, the same way <see cref="IAudioSampleProcessor.Prepare"/> works.
    /// </summary>
    public void Prepare(int channels, int sourceRate, int targetRate)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (sourceRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRate));
        if (targetRate <= 0) throw new ArgumentOutOfRangeException(nameof(targetRate));

        _channels = channels;
        _nominalRatio = (double)sourceRate / targetRate;
        _ratio = _nominalRatio;

        _previousFrame = new float[channels];
        _nextFrame = new float[channels];

        Reset();
    }

    /// <summary>Clears interpolation state. Call on stream start and after a dropout, same as the DSP processor.</summary>
    public void Reset()
    {
        _primed = false;
        _phase = 0.0;
        Array.Clear(_previousFrame, 0, _previousFrame.Length);
        Array.Clear(_nextFrame, 0, _nextFrame.Length);
    }

    /// <summary>
    /// Fills <paramref name="destination"/> with exactly as many interleaved frames as it
    /// holds, pulling whatever number of source frames that takes out of
    /// <paramref name="source"/> at the current <see cref="Ratio"/>. A source that runs dry
    /// mid-pull comes back as silence and an underrun, exactly as a direct
    /// <see cref="AudioRingBuffer.Read"/> would - this method never blocks and never
    /// invents data of its own.
    ///
    /// Real-time thread: no allocations, no locks, no I/O.
    /// </summary>
    public void Render(AudioRingBuffer source, Span<float> destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (destination.Length == 0) return;

        int destinationFrames = destination.Length / _channels;
        if (destinationFrames == 0) return;

        if (IsUnity)
        {
            // Exact passthrough: no interpolation math touches the samples at all, so
            // unchanged audio is bit-identical going in and coming out. Interpolation
            // state is dropped rather than kept "just in case" - if the ratio moves away
            // from unity again later it restarts cleanly from the ring's current position
            // instead of resuming from frames that may be long gone.
            source.Read(destination);
            _primed = false;
            _phase = 0.0;
            return;
        }

        if (!_primed)
        {
            source.Read(_previousFrame);
            source.Read(_nextFrame);
            _primed = true;
            _phase = 0.0;
        }

        double ratio = _ratio;
        int channels = _channels;

        for (int frame = 0; frame < destinationFrames; frame++)
        {
            while (_phase >= 1.0)
            {
                Array.Copy(_nextFrame, _previousFrame, channels);
                source.Read(_nextFrame);
                _phase -= 1.0;
            }

            int baseIndex = frame * channels;
            float t = (float)_phase;
            for (int channel = 0; channel < channels; channel++)
            {
                float previous = _previousFrame[channel];
                float next = _nextFrame[channel];
                destination[baseIndex + channel] = previous + (next - previous) * t;
            }

            _phase += ratio;
        }
    }
}
