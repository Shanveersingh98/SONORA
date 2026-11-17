namespace AudioEnhancer.Audio;

/// <summary>
/// What a playback endpoint told us about itself. Read-only: asking a device to
/// describe its shared-mode format changes nothing on the machine.
/// </summary>
/// <param name="Device">Identity of the endpoint.</param>
/// <param name="MixFormat">
/// The format the Windows audio engine mixes at for this device. In shared mode
/// every application is resampled to this, so it is the format the DSP engine will
/// be prepared with: <c>Prepare(MixFormat.SampleRate, MixFormat.Channels)</c>.
/// </param>
/// <param name="DefaultPeriod">The engine's normal buffer interval, typically 10 ms.</param>
/// <param name="MinimumPeriod">The shortest interval the device will accept, typically 3 ms.</param>
public sealed record AudioDeviceCapabilities(
    AudioDeviceInfo Device,
    AudioStreamFormat MixFormat,
    TimeSpan DefaultPeriod,
    TimeSpan MinimumPeriod)
{
    /// <summary>
    /// True when the DSP engine can process this device's audio without any
    /// sample-format conversion at all.
    /// </summary>
    public bool IsDspNativeFormat => MixFormat.IsFloat;

    public override string ToString() =>
        $"{Device.Name}: {MixFormat}, period {DefaultPeriod.TotalMilliseconds:0.#} ms " +
        $"(min {MinimumPeriod.TotalMilliseconds:0.#} ms)";
}

/// <summary>
/// The extra things a real Windows device manager can do beyond the Part 1
/// <see cref="IOutputDeviceManager"/> contract. Kept as a separate interface so the
/// existing one - and therefore the existing view-model - did not have to change.
/// </summary>
public interface IOutputDeviceProbe
{
    /// <summary>
    /// Asks one endpoint for its shared-mode format and timing.
    /// Returns null when the device has gone away or cannot be queried.
    /// </summary>
    AudioDeviceCapabilities? GetCapabilities(string deviceId);

    /// <summary>Re-reads the device list from Windows. Safe to call at any time.</summary>
    void Refresh();

    /// <summary>Raised when a device is added, removed, enabled, disabled or unplugged.</summary>
    event EventHandler? DevicesChanged;
}
