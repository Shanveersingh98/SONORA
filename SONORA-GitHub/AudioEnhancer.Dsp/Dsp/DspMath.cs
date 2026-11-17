namespace AudioEnhancer.Dsp;

public static class DspMath
{
    public const float DenormalFloor = 1e-20f;

    public static float DbToLinear(float db) => MathF.Pow(10.0f, db / 20.0f);

    public static float LinearToDb(float linear) =>
        linear <= 1e-9f ? -180.0f : 20.0f * MathF.Log10(linear);

    /// <summary>One-pole smoothing coefficient for a given time constant.</summary>
    public static float SmoothingCoefficient(float milliseconds, int sampleRate)
    {
        if (milliseconds <= 0.0f) return 0.0f;
        return MathF.Exp(-1.0f / (milliseconds * 0.001f * sampleRate));
    }

    /// <summary>Kills denormals, which otherwise slow filter tails to a crawl.</summary>
    public static float FlushDenormal(float value) =>
        MathF.Abs(value) < DenormalFloor ? 0.0f : value;

    /// <summary>Smooth odd-symmetric saturation. Cheap: no exp or tanh call.</summary>
    public static float SoftSaturate(float x) => x / (1.0f + MathF.Abs(x));
}
