namespace AudioEnhancer.Dsp;

/// <summary>Normalised biquad coefficients (a0 already divided out).</summary>
public struct BiquadCoefficients
{
    public float B0, B1, B2, A1, A2;

    public static BiquadCoefficients Identity => new() { B0 = 1f, B1 = 0f, B2 = 0f, A1 = 0f, A2 = 0f };

    private static BiquadCoefficients Normalise(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        if (a0 == 0.0 || double.IsNaN(a0)) return Identity;
        double inv = 1.0 / a0;
        return new BiquadCoefficients
        {
            B0 = (float)(b0 * inv), B1 = (float)(b1 * inv), B2 = (float)(b2 * inv),
            A1 = (float)(a1 * inv), A2 = (float)(a2 * inv)
        };
    }

    // Keep f0 away from Nyquist, where the design equations blow up.
    private static double SafeOmega(float frequencyHz, int sampleRate)
    {
        double f0 = Math.Clamp(frequencyHz, 10.0, sampleRate * 0.45);
        return 2.0 * Math.PI * f0 / sampleRate;
    }

    /// <summary>RBJ low shelf: boosts or cuts everything below the corner frequency.</summary>
    public static BiquadCoefficients LowShelf(float frequencyHz, float gainDb, int sampleRate, float slope = 1.0f)
    {
        double a = Math.Pow(10.0, gainDb / 40.0);
        double w0 = SafeOmega(frequencyHz, sampleRate);
        double cw = Math.Cos(w0), sw = Math.Sin(w0);
        double s = Math.Clamp(slope, 0.1, 2.0);
        double alpha = sw / 2.0 * Math.Sqrt((a + 1.0 / a) * (1.0 / s - 1.0) + 2.0);
        double k = 2.0 * Math.Sqrt(a) * alpha;

        return Normalise(
            a * ((a + 1.0) - (a - 1.0) * cw + k),
            2.0 * a * ((a - 1.0) - (a + 1.0) * cw),
            a * ((a + 1.0) - (a - 1.0) * cw - k),
            (a + 1.0) + (a - 1.0) * cw + k,
            -2.0 * ((a - 1.0) + (a + 1.0) * cw),
            (a + 1.0) + (a - 1.0) * cw - k);
    }

    /// <summary>RBJ high shelf: boosts or cuts everything above the corner frequency.</summary>
    public static BiquadCoefficients HighShelf(float frequencyHz, float gainDb, int sampleRate, float slope = 1.0f)
    {
        double a = Math.Pow(10.0, gainDb / 40.0);
        double w0 = SafeOmega(frequencyHz, sampleRate);
        double cw = Math.Cos(w0), sw = Math.Sin(w0);
        double s = Math.Clamp(slope, 0.1, 2.0);
        double alpha = sw / 2.0 * Math.Sqrt((a + 1.0 / a) * (1.0 / s - 1.0) + 2.0);
        double k = 2.0 * Math.Sqrt(a) * alpha;

        return Normalise(
            a * ((a + 1.0) + (a - 1.0) * cw + k),
            -2.0 * a * ((a - 1.0) + (a + 1.0) * cw),
            a * ((a + 1.0) + (a - 1.0) * cw - k),
            (a + 1.0) - (a - 1.0) * cw + k,
            2.0 * ((a - 1.0) - (a + 1.0) * cw),
            (a + 1.0) - (a - 1.0) * cw - k);
    }

    /// <summary>RBJ peaking EQ: a bell centred on the given frequency.</summary>
    public static BiquadCoefficients Peaking(float frequencyHz, float gainDb, float q, int sampleRate)
    {
        double a = Math.Pow(10.0, gainDb / 40.0);
        double w0 = SafeOmega(frequencyHz, sampleRate);
        double cw = Math.Cos(w0), sw = Math.Sin(w0);
        double alpha = sw / (2.0 * Math.Clamp(q, 0.1, 20.0));

        return Normalise(
            1.0 + alpha * a, -2.0 * cw, 1.0 - alpha * a,
            1.0 + alpha / a, -2.0 * cw, 1.0 - alpha / a);
    }
}

/// <summary>Per-channel filter memory. Transposed direct form II: two state words, good float behaviour.</summary>
public struct BiquadState
{
    private float _s1, _s2;

    public void Reset() { _s1 = 0f; _s2 = 0f; }

    public float Process(float x, in BiquadCoefficients c)
    {
        float y = c.B0 * x + _s1;
        _s1 = DspMath.FlushDenormal(c.B1 * x - c.A1 * y + _s2);
        _s2 = DspMath.FlushDenormal(c.B2 * x - c.A2 * y);
        return y;
    }
}
