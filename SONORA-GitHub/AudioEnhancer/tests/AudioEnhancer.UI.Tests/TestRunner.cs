namespace AudioEnhancer.UI.Tests;

/// <summary>
/// Same hand-rolled harness style as the DSP and Audio test projects, so the UI layer
/// also pulls in zero NuGet packages. Exit code 0 means everything passed.
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
            Console.WriteLine();
            Console.WriteLine("All UI view-model tests passed.");
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

    public static void IsFalse(bool condition, string message)
    {
        if (condition) throw new Exception(message);
    }

    public static void AreEqual(int actual, int expected, string what)
    {
        if (actual != expected)
            throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    public static void AreEqual(double actual, double expected, string what)
    {
        if (actual != expected)
            throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    public static void AreEqual(string? actual, string? expected, string what)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new Exception($"{what}: expected '{expected}', got '{actual}'");
    }
}
