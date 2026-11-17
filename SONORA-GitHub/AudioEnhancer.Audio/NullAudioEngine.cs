using AudioEnhancer.Core;

namespace AudioEnhancer.Audio;

/// <summary>
/// Placeholder engine for Part 1. It deliberately does NOTHING - no fake processing.
/// It exists so the UI has something to talk to. Part 2 replaces it in App.xaml.cs.
/// </summary>
public sealed class NullAudioEngine : IAudioEngine
{
    public EngineState State => EngineState.NotAvailable;
    public string StatusMessage => "Preview mode - audio engine arrives in Part 2";

    public event EventHandler? StateChanged { add { } remove { } }

    public void Start() { }
    public void Stop() { }
    public void UpdateSettings(EnhancerSettings settings) { }
    public bool Bypass { get; set; }

    /// <summary>No-op: there is no real output to point anywhere.</summary>
    public void SetOutputDevice(string? deviceId) { }

    /// <summary>
    /// Part 4 Step 7 (no-WDK application prep). No real device list exists in preview
    /// mode, so this truthfully reports NotInstalled rather than guessing - matching
    /// every other member of this placeholder engine.
    /// </summary>
    public VirtualEndpointStatusReport GetVirtualEndpointStatus() =>
        new(VirtualEndpointStatus.NotInstalled, Device: null,
            Message: "No audio engine is attached yet (preview mode).");

    /// <summary>No-op: there is nothing to select in preview mode.</summary>
    public VirtualEndpointCaptureOutcome TryUseVirtualEndpointForCapture() =>
        new(VirtualEndpointCaptureResult.NotInstalled, "No audio engine is attached yet (preview mode).");

    public void SetMasterVolume(float scalar) { }
    public float? GetMasterVolume() => null;

    public void SetMasterMute(bool mute) { }
    public bool? GetMasterMute() => false;

    public void GetSpectrumAnalysis(Span<float> outBands, out float outPeakDb, out float outRmsDb, out bool isAudioActive)
    {
        outBands.Clear();
        outPeakDb = -120f;
        outRmsDb = -120f;
        isAudioActive = false;
    }

    public void Dispose() { }
}
