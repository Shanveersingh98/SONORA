namespace AudioEnhancer.Audio;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault, bool IsHeadphoneHardware = false)
{
    /// <summary>
    /// Returns true when this device represents headphones, a headset, earbuds, or in-ear monitors.
    /// Used for automatic headphone audio routing and clean analog stage calibration.
    /// </summary>
    public bool IsHeadphones =>
        IsHeadphoneHardware ||
        (!string.IsNullOrWhiteSpace(Name) &&
        (Name.Contains("headphone", StringComparison.OrdinalIgnoreCase) ||
         Name.Contains("headset", StringComparison.OrdinalIgnoreCase) ||
         Name.Contains("earphone", StringComparison.OrdinalIgnoreCase) ||
         Name.Contains("airpod", StringComparison.OrdinalIgnoreCase) ||
         Name.Contains("buds", StringComparison.OrdinalIgnoreCase) ||
         Name.Contains("earbud", StringComparison.OrdinalIgnoreCase) ||
         Name.Contains("in-ear", StringComparison.OrdinalIgnoreCase) ||
         Name.Contains("iem", StringComparison.OrdinalIgnoreCase) ||
         Name.Contains("bluetooth", StringComparison.OrdinalIgnoreCase) ||
         Name.Contains("hands-free", StringComparison.OrdinalIgnoreCase) ||
         Name.Contains("wh-", StringComparison.OrdinalIgnoreCase) ||
         Name.Contains("wf-", StringComparison.OrdinalIgnoreCase) ||
         Name.Contains("quietcomfort", StringComparison.OrdinalIgnoreCase) ||
         Name.Contains("beats", StringComparison.OrdinalIgnoreCase) ||
         Name.Contains("sennheiser", StringComparison.OrdinalIgnoreCase) ||
         Name.Contains("jabra", StringComparison.OrdinalIgnoreCase)));
}

/// <summary>
/// Finds the Windows playback devices (speakers, headphones, USB, Bluetooth).
/// Part 2 implements this with the Windows Core Audio device enumerator.
/// </summary>
public interface IOutputDeviceManager
{
    AudioDeviceInfo GetDefaultOutput();
    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();

    /// <summary>Raised when the user changes the default playback device in Windows.</summary>
    event EventHandler? DefaultOutputChanged;

    /// <summary>Raised when physical audio devices are plugged in, disconnected, or changed.</summary>
    event EventHandler? DevicesChanged;
}
