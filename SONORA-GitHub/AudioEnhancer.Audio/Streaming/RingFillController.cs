namespace AudioEnhancer.Audio;

/// <summary>
/// Watches how full the ring buffer is and turns that into a small correction for
/// <see cref="AdaptiveResampler.Ratio"/>, so two independent hardware clocks - the
/// capture device's and the render device's - can disagree by a few hundred parts per
/// million forever without the ring slowly draining or overflowing.
///
/// <para><b>Why proportional, not integrating.</b> The ring's fill level is already the
/// running total of every past write minus every past read, which makes it an
/// integrator in its own right. Feeding that back through a second integrator (accumulate
/// the fill error, adjust the ratio by the accumulated amount) stacks two integrators in
/// one loop, which has no damping term and drifts into a slow oscillation - each swing
/// larger than the last - rather than settling. A plain proportional response,
/// <c>correction = clamp(gain * (fill - target), -max, max)</c>, is a single first-order
/// system: given a constant clock-drift disturbance it settles at a fixed, bounded offset
/// from the target fill level, and the correction it converges to is exactly the drift
/// being corrected for. That is a small permanent difference from the target, not a
/// growing one, and it is far simpler to reason about than tuning a damped PI loop for a
/// value nobody reads except this class.</para>
///
/// <para><b>"Slowly"</b> comes from the gain, not from smoothing the output: the default
/// is small enough that one render callback's worth of fill error moves the ratio by a
/// tiny, inaudible fraction, and it only adds up to a meaningful correction over many
/// seconds of consistent drift in one direction. A sudden one-off change in fill level -
/// a device glitch, a dropout being recovered from - therefore nudges the ratio by very
/// little, rather than snapping it.</para>
///
/// <para>Not thread-safe by design: exactly one thread (the render thread, in the real
/// stream) ever calls <see cref="Update"/> or reads <see cref="Correction"/>, the same
/// rule <see cref="AdaptiveResampler"/> follows.</para>
/// </summary>
public sealed class RingFillController
{
    /// <summary>
    /// Fractional ratio correction per sample of fill error. Chosen so that correcting a
    /// realistic few-hundred-ppm clock drift settles the ring within a few hundred
    /// samples of its target - a rounding error against any buffer sized in Part 2C's
    /// "four device buffers" terms - while a single callback's worth of fill error moves
    /// the ratio by a small fraction of that.
    /// </summary>
    public const double DefaultGain = 1e-6;

    /// <summary>
    /// Hard ceiling on the correction, about fifty times wider than the ±200 ppm this was
    /// built to correct. It exists so a control loop bug or a wildly misbehaving device
    /// cannot turn a clock-drift correction into an audible pitch shift.
    /// </summary>
    public const double DefaultMaxCorrection = 0.01;

    private readonly int _targetFillSamples;
    private readonly double _gain;
    private readonly double _maxCorrection;

    private double _correction;
    private bool _active;

    public RingFillController(int targetFillSamples, double gain = DefaultGain, double maxCorrection = DefaultMaxCorrection)
    {
        if (targetFillSamples < 0) throw new ArgumentOutOfRangeException(nameof(targetFillSamples));
        if (gain <= 0 || !double.IsFinite(gain)) throw new ArgumentOutOfRangeException(nameof(gain));
        if (maxCorrection <= 0 || !double.IsFinite(maxCorrection)) throw new ArgumentOutOfRangeException(nameof(maxCorrection));

        _targetFillSamples = targetFillSamples;
        _gain = gain;
        _maxCorrection = maxCorrection;
    }

    /// <summary>The fill level, in samples, this controller is steering the ring towards.</summary>
    public int TargetFillSamples => _targetFillSamples;

    /// <summary>
    /// The current correction: add 1.0 to it and multiply <see cref="AdaptiveResampler.NominalRatio"/>
    /// by the result. Zero until the first <see cref="Update"/> call while active.
    /// </summary>
    public double Correction => _correction;

    /// <summary>True while <see cref="Update"/> is actually adjusting <see cref="Correction"/>.</summary>
    public bool IsActive => _active;

    /// <summary>
    /// Starts or freezes adjustment. While inactive, <see cref="Update"/> is a no-op and
    /// <see cref="Correction"/> holds whatever value it last had - it does not snap back
    /// to zero, because that would itself be an audible step the moment the stream
    /// resumes. The stream is expected to call this with <c>true</c> once it starts
    /// running and never call it again for that session; a fresh session gets a fresh
    /// controller, which is what actually resets it.
    /// </summary>
    public void SetActive(bool active) => _active = active;

    /// <summary>Zeroes the correction outright. Not called by the stream in normal operation - a new session builds a new controller instead - but useful for tests.</summary>
    public void Reset() => _correction = 0.0;

    /// <summary>
    /// Tells the controller how full the ring is right now, in samples. While active, this
    /// recomputes <see cref="Correction"/> from scratch as a clamped proportional response
    /// to the fill error; while frozen it does nothing at all.
    /// </summary>
    public void Update(int ringFillSamples)
    {
        if (!_active) return;

        double error = ringFillSamples - _targetFillSamples;
        double target = _gain * error;
        _correction = Math.Clamp(target, -_maxCorrection, _maxCorrection);
    }
}
