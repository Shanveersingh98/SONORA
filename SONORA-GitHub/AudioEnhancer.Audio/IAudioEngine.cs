using AudioEnhancer.Core;

namespace AudioEnhancer.Audio;

public enum EngineState { NotAvailable, Stopped, Starting, Running, Faulted }

/// <summary>
/// Everything the UI is allowed to know about the audio engine.
/// Part 2 will add a real implementation (WASAPI capture -> DSP -> WASAPI render).
/// The UI only ever talks to this interface, never to a concrete engine.
/// </summary>
public interface IAudioEngine : IDisposable
{
    EngineState State { get; }
    string StatusMessage { get; }

    /// <summary>Raised when State or StatusMessage changes (may come from a non-UI thread).</summary>
    event EventHandler? StateChanged;

    void Start();
    void Stop();

    /// <summary>Called whenever a slider or preset changes.</summary>
    void UpdateSettings(EnhancerSettings settings);

    /// <summary>
    /// When true, audio passes straight through with DSP bypassed.
    /// Used when enhancement is switched OFF while keeping the audio stream alive.
    /// </summary>
    bool Bypass { get; set; }

    /// <summary>
    /// Part 4 Step 6: which endpoint the enhanced audio is played to. Null means "the
    /// default". Exposes the same call <see cref="AudioEnhancer.Audio.WasapiAudioEngine"/>
    /// already implements internally, so the UI's device picker can reach it through the
    /// interface instead of a concrete-class cast. Takes effect on the next Start.
    /// </summary>
    void SetOutputDevice(string? deviceId);

    /// <summary>
    /// Part 4 Step 7 (no-WDK application prep). Where the "AudioEnhancer Virtual Output"
    /// endpoint stands right now, in terms the UI can display truthfully. Exposes the same
    /// call <see cref="AudioEnhancer.Audio.WasapiAudioEngine.GetVirtualEndpointStatus"/> has
    /// implemented since Part 4 Step 2, through the interface the UI actually depends on -
    /// the same reasoning Step 6 already applied to <see cref="SetOutputDevice"/> above.
    /// Always reports <see cref="VirtualEndpointStatus.NotInstalled"/> today: no driver has
    /// been built, signed, or installed by this application or this repository.
    /// </summary>
    VirtualEndpointStatusReport GetVirtualEndpointStatus();

    /// <summary>
    /// Part 4 Step 7 (no-WDK application prep). If the virtual endpoint is currently
    /// installed, points this engine's capture side at it - nothing more. See
    /// <see cref="AudioEnhancer.Audio.WasapiAudioEngine.TryUseVirtualEndpointForCapture"/>
    /// for the full contract (opt-in only, never called automatically); this is that same
    /// call, reachable through the interface instead of a concrete-class cast.
    /// </summary>
    VirtualEndpointCaptureOutcome TryUseVirtualEndpointForCapture();

    /// <summary>
    /// Master volume level scalar (0.0 to 1.0).
    /// Allows the in-app volume slider to directly adjust system playback volume.
    /// </summary>
    void SetMasterVolume(float scalar);
    float? GetMasterVolume();

    /// <summary>
    /// Master mute state.
    /// Enables laptop keyboard mute key (F3) and in-app mute synchronization.
    /// </summary>
    void SetMasterMute(bool mute);
    bool? GetMasterMute();

    /// <summary>
    /// Reads the latest real-time 32-band FFT frequency spectrum, true peak, and RMS energy.
    /// Thread-safe observation tap. Does not allocate or block the audio thread.
    /// </summary>
    void GetSpectrumAnalysis(Span<float> outBands, out float outPeakDb, out float outRmsDb, out bool isAudioActive);
}
