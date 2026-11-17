namespace AudioEnhancer.Dsp.Testing;

/// <summary>
/// Deterministic test signals. This is the testable path: it lets us prove the
/// processors really change samples without touching an audio device.
/// </summary>
public static class SignalGenerator
{
    public static float[] Silence(int frameCount, int channels) => new float[frameCount * channels];

    public static float[] Sine(float frequencyHz, float amplitude, int frameCount, int sampleRate, int channels)
    {
        var buffer = new float[frameCount * channels];
        double step = 2.0 * Math.PI * frequencyHz / sampleRate;
        for (int frame = 0; frame < frameCount; frame++)
        {
            float sample = (float)(amplitude * Math.Sin(step * frame));
            for (int ch = 0; ch < channels; ch++)
                buffer[frame * channels + ch] = sample;
        }
        return buffer;
    }

    /// <summary>Seeded white noise, so a failure can always be reproduced.</summary>
    public static float[] Noise(float amplitude, int frameCount, int channels, int seed = 1234)
    {
        var random = new Random(seed);
        var buffer = new float[frameCount * channels];
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = (float)((random.NextDouble() * 2.0 - 1.0) * amplitude);
        return buffer;
    }

    public static float[] Impulse(float amplitude, int frameCount, int channels)
    {
        var buffer = new float[frameCount * channels];
        for (int ch = 0; ch < channels; ch++)
            buffer[ch] = amplitude;
        return buffer;
    }
}

public static class Measure
{
    public static float Peak(ReadOnlySpan<float> buffer)
    {
        float peak = 0f;
        foreach (float sample in buffer)
        {
            float magnitude = MathF.Abs(sample);
            if (magnitude > peak) peak = magnitude;
        }
        return peak;
    }

    public static float Rms(ReadOnlySpan<float> buffer)
    {
        if (buffer.Length == 0) return 0f;
        double sum = 0.0;
        foreach (float sample in buffer)
            sum += (double)sample * sample;
        return (float)Math.Sqrt(sum / buffer.Length);
    }

    /// <summary>RMS of the tail only, so filter start-up transients do not skew the reading.</summary>
    public static float RmsTail(ReadOnlySpan<float> buffer, float skipFraction = 0.5f) =>
        Rms(buffer[(int)(buffer.Length * skipFraction)..]);

    public static bool HasNonFinite(ReadOnlySpan<float> buffer)
    {
        foreach (float sample in buffer)
            if (float.IsNaN(sample) || float.IsInfinity(sample)) return true;
        return false;
    }

    /// <summary>Energy of the difference between two buffers: did the DSP actually do anything?</summary>
    public static float DifferenceRms(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int length = Math.Min(a.Length, b.Length);
        if (length == 0) return 0f;
        double sum = 0.0;
        for (int i = 0; i < length; i++)
        {
            double d = a[i] - b[i];
            sum += d * d;
        }
        return (float)Math.Sqrt(sum / length);
    }
}
