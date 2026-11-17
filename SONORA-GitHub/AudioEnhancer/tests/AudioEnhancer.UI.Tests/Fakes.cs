using AudioEnhancer.Audio;
using AudioEnhancer.Core;

namespace AudioEnhancer.UI.Tests;

/// <summary>
/// A controllable <see cref="IAudioEngine"/> so MainViewModel tests never touch real
/// hardware. Same reasoning as AudioEnhancer.Audio.Tests' RecordingBackend/FaultInjectableStream:
/// the test sets whatever the engine would report and observes what the view model calls.
/// </summary>
internal sealed class FakeAudioEngine : IAudioEngine
{
    public EngineState State { get; set; } = EngineState.Stopped;
    public string StatusMessage { get; set; } = "Ready";
    public bool Bypass { get; set; }

    public event EventHandler? StateChanged;

    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public int SetOutputDeviceCallCount { get; private set; }
    public string? LastOutputDeviceId { get; private set; }
    public List<EnhancerSettings> SettingsHistory { get; } = new();

    /// <summary>Part 4 Step 7 (no-WDK): what GetVirtualEndpointStatus() should return - a test sets this directly.</summary>
    public VirtualEndpointStatusReport VirtualEndpointStatus { get; set; } =
        new(AudioEnhancer.Audio.VirtualEndpointStatus.NotInstalled, Device: null, Message: "not installed (fake default)");

    /// <summary>Part 4 Step 7 (no-WDK): what TryUseVirtualEndpointForCapture() should return - a test sets this directly.</summary>
    public VirtualEndpointCaptureOutcome VirtualEndpointCaptureOutcome { get; set; } =
        new(VirtualEndpointCaptureResult.NotInstalled, "not installed (fake default)");

    public int TryUseVirtualEndpointForCaptureCallCount { get; private set; }

    public void Start() => StartCount++;
    public void Stop() => StopCount++;

    public void UpdateSettings(EnhancerSettings settings) => SettingsHistory.Add(settings);

    public void SetOutputDevice(string? deviceId)
    {
        SetOutputDeviceCallCount++;
        LastOutputDeviceId = deviceId;
    }

    public VirtualEndpointStatusReport GetVirtualEndpointStatus() => VirtualEndpointStatus;

    public VirtualEndpointCaptureOutcome TryUseVirtualEndpointForCapture()
    {
        TryUseVirtualEndpointForCaptureCallCount++;
        return VirtualEndpointCaptureOutcome;
    }

    /// <summary>Lets a test simulate the engine reporting a new state, exactly as a real engine would fire it.</summary>
    public void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    public float? MasterVolume { get; set; }
    public void SetMasterVolume(float scalar) => MasterVolume = scalar;
    public float? GetMasterVolume() => MasterVolume;

    public bool? MasterMute { get; set; } = false;
    public void SetMasterMute(bool mute) => MasterMute = mute;
    public bool? GetMasterMute() => MasterMute;

    public void GetSpectrumAnalysis(Span<float> outBands, out float outPeakDb, out float outRmsDb, out bool isAudioActive)
    {
        outBands.Clear();
        outPeakDb = -120f;
        outRmsDb = -120f;
        isAudioActive = false;
    }

    public void Dispose() { }
}

/// <summary>
/// A mutable <see cref="IOutputDeviceManager"/> so a test can simulate a render device
/// disappearing from Windows' enumeration between one refresh and the next.
/// </summary>
internal sealed class FakeOutputDeviceManager : IOutputDeviceManager
{
    private List<AudioDeviceInfo> _devices;

    public FakeOutputDeviceManager(IEnumerable<AudioDeviceInfo> devices) => _devices = new List<AudioDeviceInfo>(devices);

    public AudioDeviceInfo GetDefaultOutput() =>
        _devices.Find(d => d.IsDefault) ?? _devices[0];

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => _devices;

    /// <summary>Never raised directly in these tests - see MainViewModel.RefreshDevicesForTests.</summary>
    public event EventHandler? DefaultOutputChanged { add { } remove { } }
    public event EventHandler? DevicesChanged { add { } remove { } }

    /// <summary>Simulates Windows' enumeration changing (e.g. a USB DAC being unplugged).</summary>
    public void SetDevices(IEnumerable<AudioDeviceInfo> devices) => _devices = new List<AudioDeviceInfo>(devices);
}
