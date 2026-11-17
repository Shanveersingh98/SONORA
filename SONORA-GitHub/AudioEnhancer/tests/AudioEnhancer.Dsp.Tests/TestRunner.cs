namespace AudioEnhancer.Dsp.Tests;

/// <summary>
/// Tiny hand-rolled harness, deliberately not xUnit or NUnit, so neither the DSP
/// library nor its tests pull in a single NuGet package. Exit code 0 means all passed.
/// </summary>
public sealed class TestRunner
{
    private int _passed;
    private readonly List<string> _failures = new();

    public void Test(string name, Action body)
    {
        try
        {
            body();
            _passed++;
            Console.WriteLine($"  PASS  {name}");
        }
        catch (Exception ex)
        {
            _failures.Add($"{name}: {ex.Message}");
            Console.WriteLine($"  FAIL  {name}");
            Console.WriteLine($"        {ex.Message}");
        }
    }

    public int Summarise()
    {
        Console.WriteLine();
        Console.WriteLine($"Passed: {_passed}   Failed: {_failures.Count}");
        if (_failures.Count == 0)
        {
            Console.WriteLine("All DSP tests passed.");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("Failures:");
        foreach (string failure in _failures)
            Console.WriteLine($"  - {failure}");
        return 1;
    }

    public static void IsTrue(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Approximately(float actual, float expected, float tolerance, string what)
    {
        if (MathF.Abs(actual - expected) > tolerance)
            throw new Exception($"{what}: expected {expected:F5} +/- {tolerance:F5}, got {actual:F5}");
    }

    public static void GreaterThan(float actual, float threshold, string what)
    {
        if (!(actual > threshold))
            throw new Exception($"{what}: expected > {threshold:F5}, got {actual:F5}");
    }

    public static void LessThanOrEqual(float actual, float threshold, string what)
    {
        if (!(actual <= threshold))
            throw new Exception($"{what}: expected <= {threshold:F5}, got {actual:F5}");
    }
}
