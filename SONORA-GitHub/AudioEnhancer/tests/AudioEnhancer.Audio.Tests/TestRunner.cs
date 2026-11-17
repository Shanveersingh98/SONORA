using System.Diagnostics.CodeAnalysis;

namespace AudioEnhancer.Audio.Tests;

/// <summary>
/// Same hand-rolled harness style as the DSP tests, so the audio backend also pulls
/// in zero NuGet packages. Exit code 0 means everything passed.
///
/// It adds one idea the DSP tests did not need: <see cref="Skipped"/>. Some of these
/// tests talk to real hardware, and a build agent or a machine with the audio service
/// stopped legitimately has no playback device. A skip is reported and does not fail
/// the run, but it is never silent.
/// </summary>
public sealed class TestRunner
{
    private int _passed;
    private readonly List<string> _failures = new();
    private readonly List<string> _skips = new();

    /// <summary>Thrown by a test that cannot run on this machine.</summary>
    public sealed class SkipException : Exception
    {
        public SkipException(string reason) : base(reason) { }
    }

    [DoesNotReturn]
    public static void Skipped(string reason) => throw new SkipException(reason);

    public void Test(string name, Action body)
    {
        try
        {
            body();
            _passed++;
            Console.WriteLine($"  PASS  {name}");
        }
        catch (SkipException skip)
        {
            _skips.Add($"{name}: {skip.Message}");
            Console.WriteLine($"  SKIP  {name}  ({skip.Message})");
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
        Console.WriteLine($"Passed: {_passed}   Failed: {_failures.Count}   Skipped: {_skips.Count}");

        if (_skips.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Skipped:");
            foreach (string skip in _skips)
                Console.WriteLine($"  - {skip}");
        }

        if (_failures.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("All audio backend tests passed.");
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

    public static void AreEqual(int actual, int expected, string what)
    {
        if (actual != expected)
            throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    public static void AreEqual(long actual, long expected, string what)
    {
        if (actual != expected)
            throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    public static void AreEqual(string? actual, string? expected, string what)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new Exception($"{what}: expected '{expected}', got '{actual}'");
    }

    public static void Approximately(double actual, double expected, double tolerance, string what)
    {
        if (Math.Abs(actual - expected) > tolerance)
            throw new Exception($"{what}: expected {expected:F6} +/- {tolerance:F6}, got {actual:F6}");
    }

    public static void GreaterThan(double actual, double threshold, string what)
    {
        if (!(actual > threshold))
            throw new Exception($"{what}: expected > {threshold:F6}, got {actual:F6}");
    }

    public static void LessThanOrEqual(double actual, double threshold, string what)
    {
        if (!(actual <= threshold))
            throw new Exception($"{what}: expected <= {threshold:F6}, got {actual:F6}");
    }

    public static void Throws<TException>(Action body, string what) where TException : Exception
    {
        try
        {
            body();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new Exception($"{what}: expected {typeof(TException).Name}, got {ex.GetType().Name}");
        }

        throw new Exception($"{what}: expected {typeof(TException).Name}, nothing was thrown");
    }
}
