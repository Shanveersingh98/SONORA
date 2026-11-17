namespace AudioEnhancer.Audio;

/// <summary>
/// Placeholder device manager: reports a single "nothing here" endpoint.
/// Part 2B added the real one (WindowsOutputDeviceManager); this stays as the
/// fallback for machines where Core Audio cannot be reached, and for tests.
/// </summary>
public sealed class NullOutputDeviceManager : IOutputDeviceManager
{
    private static readonly AudioDeviceInfo None =
        new("none", "No playback device detected", true);

    public AudioDeviceInfo GetDefaultOutput() => None;
    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => new[] { None };
    public event EventHandler? DefaultOutputChanged { add { } remove { } }
    public event EventHandler? DevicesChanged { add { } remove { } }
}
