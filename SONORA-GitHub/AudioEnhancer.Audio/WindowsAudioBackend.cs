using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AudioEnhancer.Audio;

/// <summary>
/// The platform layer the rest of the app sees. One object that knows which playback
/// devices exist, what format they run at, and - from Part 2C - how to open a stream
/// onto one of them.
/// </summary>
public interface IAudioBackend : IAudioStreamFactory, IDisposable
{
    /// <summary>False when the platform could not be reached; the app should stay in preview mode.</summary>
    bool IsAvailable { get; }

    /// <summary>One line a user can read, e.g. "Windows Core Audio (WASAPI) - 4 playback devices".</summary>
    string Description { get; }

    /// <summary>Device detection and default-device tracking.</summary>
    IOutputDeviceManager Devices { get; }

    /// <summary>Format and timing for one endpoint, or null when it cannot be queried.</summary>
    AudioDeviceCapabilities? GetCapabilities(string deviceId);

    /// <summary>Format and timing for whatever Windows is currently playing through.</summary>
    AudioDeviceCapabilities? GetDefaultCapabilities();
}

/// <summary>
/// Windows Core Audio backend.
///
/// Enumerates devices, reports their shared-mode formats and timing, and - since
/// Part 2C - opens the real WASAPI stream through <see cref="Create"/>.
///
/// Nothing here installs a driver, creates a virtual device, injects into another
/// process or writes a Windows setting.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAudioBackend : IAudioBackend
{
    private readonly WindowsOutputDeviceManager _devices;
    private bool _disposed;

    public WindowsAudioBackend()
    {
        _devices = new WindowsOutputDeviceManager();
    }

    /// <summary>True on Windows with a reachable audio service. Checked before constructing.</summary>
    public static bool IsSupportedOnThisMachine =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public bool IsAvailable => !_disposed && _devices.IsAvailable;

    public IOutputDeviceManager Devices => _devices;

    /// <summary>The same object, typed for the extra probing and refresh members.</summary>
    public IOutputDeviceProbe Probe => _devices;

    public string Description
    {
        get
        {
            if (_disposed) return "Audio backend disposed";
            if (!_devices.IsAvailable)
            {
                string reason = string.IsNullOrEmpty(_devices.LastError) ? "unavailable" : _devices.LastError;
                return $"Windows Core Audio unavailable ({reason})";
            }

            int count = _devices.GetOutputDevices().Count;
            string plural = count == 1 ? "device" : "devices";
            return $"Windows Core Audio (WASAPI) - {count} playback {plural}";
        }
    }

    public AudioDeviceCapabilities? GetCapabilities(string deviceId) => _devices.GetCapabilities(deviceId);

    public AudioDeviceCapabilities? GetDefaultCapabilities() => _devices.GetDefaultCapabilities();

    /// <summary>
    /// Every active playback device with its format, for diagnostics and for the
    /// device picker a later part will add.
    /// </summary>
    public IReadOnlyList<AudioDeviceCapabilities> DescribeAll()
    {
        var described = new List<AudioDeviceCapabilities>();
        foreach (AudioDeviceInfo device in _devices.GetOutputDevices())
        {
            AudioDeviceCapabilities? capabilities = _devices.GetCapabilities(device.Id);
            if (capabilities is not null) described.Add(capabilities);
        }
        return described;
    }

    /// <summary>
    /// Opens the real signal path: loopback capture of the system mix, through the ring
    /// buffer and the DSP, out to the chosen device. The stream is created stopped;
    /// call <see cref="IAudioStream.Start"/> to set it running.
    /// </summary>
    public IAudioStream Create(AudioStreamConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_devices.IsAvailable)
            throw new AudioBackendException($"Windows Core Audio is unavailable: {Description}");

        return new WasapiAudioStream(configuration, _devices);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _devices.Dispose();
    }
}

/// <summary>
/// Stand-in for when Core Audio cannot be used - a non-Windows machine, or a Windows
/// one where the enumerator would not start. Mirrors the Part 1 Null* placeholders:
/// it reports honestly that there is no platform underneath rather than pretending.
/// </summary>
public sealed class NullAudioBackend : IAudioBackend
{
    private readonly NullOutputDeviceManager _devices = new();

    public NullAudioBackend(string? reason = null) =>
        Description = string.IsNullOrWhiteSpace(reason)
            ? "No audio backend available"
            : $"No audio backend available ({reason})";

    public bool IsAvailable => false;

    public string Description { get; }

    public IOutputDeviceManager Devices => _devices;

    public AudioDeviceCapabilities? GetCapabilities(string deviceId) => null;

    public AudioDeviceCapabilities? GetDefaultCapabilities() => null;

    public IAudioStream Create(AudioStreamConfiguration configuration) =>
        throw new AudioBackendException("No audio backend is available on this machine.");

    public void Dispose() { }
}

/// <summary>Picks the right backend for the machine the app is running on.</summary>
public static class AudioBackend
{
    /// <summary>
    /// Returns a working Windows backend, or a <see cref="NullAudioBackend"/> when one
    /// cannot be created. Never throws, so it is safe to call from application startup.
    /// </summary>
    public static IAudioBackend CreateForCurrentPlatform()
    {
        if (!WindowsAudioBackend.IsSupportedOnThisMachine)
            return new NullAudioBackend("this is not Windows");

        try
        {
            var backend = new WindowsAudioBackend();
            if (backend.IsAvailable) return backend;

            string reason = backend.Description;
            backend.Dispose();
            return new NullAudioBackend(reason);
        }
        catch (Exception ex)
        {
            return new NullAudioBackend(ex.Message);
        }
    }
}
