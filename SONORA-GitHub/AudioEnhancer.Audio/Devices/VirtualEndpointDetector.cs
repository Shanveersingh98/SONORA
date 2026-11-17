namespace AudioEnhancer.Audio;

/// <summary>
/// Part 4 scaffolding only - not called from anywhere yet.
///
/// Once a Part 4 Step 2 virtual playback endpoint exists, it will enumerate through
/// <see cref="IOutputDeviceManager"/> exactly like any real device (a virtual endpoint
/// still has to register through the normal Windows Core Audio APIs to be usable, so
/// nothing about enumeration changes). This class is the one place that will know how
/// to pick that endpoint out of the list, so the recognition rule lives in one spot
/// instead of being duplicated wherever a caller wants "the virtual one".
///
/// It intentionally does not implement any driver, does not install anything, and does
/// not change how <see cref="WindowsOutputDeviceManager"/> enumerates devices.
/// </summary>
public static class VirtualEndpointDetector
{
    /// <summary>
    /// The friendly-name fragment the Step 2 driver is expected to expose. Kept as a
    /// constant here so it only has to be updated in one place once the driver's real
    /// device name is known.
    /// </summary>
    public const string ExpectedNameFragment = "AudioEnhancer Virtual Output";

    /// <summary>
    /// The friendly name actually exposed by the installed virtual endpoint (the SYSVAD
    /// "Tablet Sample" render device). Recognized alongside <see cref="ExpectedNameFragment"/>
    /// so detection works against the real device name without dropping the original
    /// placeholder match.
    /// </summary>
    public const string TabletSampleNameFragment = "Virtual Audio Device (WDM) - Tablet Sample";

    /// <summary>
    /// The preferred render endpoint exposed by SYSVAD. The driver currently exposes
    /// several render endpoints with the same generic friendly-name fragment; the
    /// Speakers endpoint is the one intended for the system-audio capture path.
    /// </summary>
    public const string PreferredTabletSampleName = "Speakers (Virtual Audio Device (WDM) - Tablet Sample)";

    /// <summary>
    /// Friendly-name fragments recognized as valid virtual endpoints for loopback capture.
    /// Supports Sonora, AudioEnhancer Virtual Output, SYSVAD, and signed virtual devices like FxSound.
    /// </summary>
    public const string SonoraNameFragment = "Sonora";
    public const string FxSoundNameFragment = "FxSound";
    public const string VbAudioNameFragment = "VB-Audio";

    /// <summary>
    /// True if <paramref name="device"/> looks like an AudioEnhancer-compatible virtual endpoint.
    /// </summary>
    public static bool IsVirtualEndpoint(AudioDeviceInfo device)
    {
        if (device is null) return false;
        return device.Name.Contains(SonoraNameFragment, StringComparison.OrdinalIgnoreCase)
            || device.Name.Contains(ExpectedNameFragment, StringComparison.OrdinalIgnoreCase)
            || device.Name.Contains(TabletSampleNameFragment, StringComparison.OrdinalIgnoreCase)
            || device.Name.Contains(FxSoundNameFragment, StringComparison.OrdinalIgnoreCase)
            || device.Name.Contains(VbAudioNameFragment, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds the virtual endpoint among the devices an <see cref="IOutputDeviceManager"/>
    /// reports, or null if none is present.
    /// </summary>
    public static AudioDeviceInfo? Find(IReadOnlyList<AudioDeviceInfo> devices)
    {
        // 1. Prioritize the Sonora Speakers device.
        foreach (var device in devices)
        {
            if (device.Name.Contains(SonoraNameFragment, StringComparison.OrdinalIgnoreCase))
                return device;
        }

        // 2. Custom AudioEnhancer Virtual Output device.
        foreach (var device in devices)
        {
            if (device.Name.Contains(ExpectedNameFragment, StringComparison.OrdinalIgnoreCase))
                return device;
        }

        // 2. FxSound Speakers endpoint (digitally signed, clean virtual sink)
        foreach (var device in devices)
        {
            if (device.Name.Contains(FxSoundNameFragment, StringComparison.OrdinalIgnoreCase))
                return device;
        }

        // 3. Fall back to the SYSVAD Speakers endpoint when only Tablet Sample is present.
        foreach (var device in devices)
        {
            if (device.Name.Equals(PreferredTabletSampleName, StringComparison.OrdinalIgnoreCase))
                return device;
        }

        // 4. Fall back to VB-Audio or any other recognized virtual endpoints.
        foreach (var device in devices)
        {
            if (IsVirtualEndpoint(device)) return device;
        }

        return null;
    }
}
