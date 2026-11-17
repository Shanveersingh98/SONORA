namespace AudioEnhancer.Audio;

/// <summary>Where a stream's samples come from or go to.</summary>
public enum AudioStreamDirection
{
    /// <summary>We hand samples to a playback device.</summary>
    Render,

    /// <summary>We take samples from an input device.</summary>
    Capture,

    /// <summary>
    /// We take a copy of what a playback device is already playing. This is the mode the
    /// enhancer will use, because it needs the system mix, and it is a documented
    /// WASAPI flag - no driver, no virtual device, no injection.
    /// </summary>
    Loopback,
}

/// <summary>Lifecycle of a stream. Only ever moves forwards, apart from Running -&gt; Open on stop.</summary>
public enum AudioStreamState
{
    Closed,
    Opening,
    Open,
    Running,
    Stopping,
    Faulted,
}

/// <summary>
/// Everything needed to open one stream. A record so it can be handed across threads
/// without anyone worrying about it changing underneath them.
/// </summary>
/// <param name="DeviceId">Endpoint to use, or null for whatever Windows is defaulting to.</param>
/// <param name="Direction">Render, capture or loopback.</param>
/// <param name="BufferDuration">
/// How much audio the device buffer should hold. Smaller means lower delay and a
/// higher risk of dropouts. 20 ms is a safe default for shared mode.
/// </param>
/// <param name="UseEventCallback">
/// True to have the device wake us when it needs audio, false to poll on a timer.
/// Event-driven is lower latency and is what Part 2C will use.
/// </param>
/// <param name="FollowDefaultDevice">
/// True to move the stream automatically when the user changes the default playback
/// device in Windows. Only meaningful when DeviceId is null.
/// </param>
/// <param name="CaptureDeviceId">
/// The playback endpoint whose output is captured by device-loopback, or null to capture
/// the current default playback device. Ignored when <see cref="UseProcessLoopbackCapture"/>
/// is true.
/// </param>
public sealed record AudioStreamConfiguration(
    string? DeviceId = null,
    AudioStreamDirection Direction = AudioStreamDirection.Loopback,
    TimeSpan BufferDuration = default,
    bool UseEventCallback = true,
    bool FollowDefaultDevice = true,
    string? CaptureDeviceId = null)
{
    /// <summary>
    /// When true, capture the Windows process-loopback mix instead of loopback from one
    /// physical playback endpoint. The target process tree is excluded from capture so
    /// AudioEnhancer cannot capture its own rendered output.
    /// </summary>
    public bool UseProcessLoopbackCapture { get; init; }

    /// <summary>
    /// Process tree to exclude from process-loopback capture. Zero means the current
    /// AudioEnhancer process when the process-loopback mode is enabled.
    /// </summary>
    public uint ProcessLoopbackTargetProcessId { get; init; }

    /// <summary>The buffer duration to actually use, with the default filled in.</summary>
    public TimeSpan EffectiveBufferDuration =>
        BufferDuration > TimeSpan.Zero ? BufferDuration : TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Ring-buffer capacity in samples for this configuration: four device buffers'
    /// worth, which absorbs one late callback on either side without dropping audio.
    /// </summary>
    public int RingBufferSamples(AudioStreamFormat format)
    {
        int frames = format.FramesFor(EffectiveBufferDuration);
        int samples = frames * format.Channels * 4;
        return samples < 2 ? 2 : samples;
    }
}

/// <summary>What a running stream is doing. Read for meters and diagnostics, never for control flow.</summary>
/// <param name="FramesProcessed">Frames that have been through the processor.</param>
/// <param name="Overruns">Samples dropped because the consumer fell behind.</param>
/// <param name="Underruns">Samples of silence inserted because the producer fell behind.</param>
/// <param name="LatencyFrames">End-to-end delay, device buffer plus processor look-ahead.</param>
public readonly record struct AudioStreamStatistics(
    long FramesProcessed,
    long Overruns,
    long Underruns,
    int LatencyFrames)
{
    public bool IsClean => Overruns == 0 && Underruns == 0;
}

/// <summary>Thrown when the backend cannot do what was asked. Always carries a message a user can read.</summary>
public sealed class AudioBackendException : Exception
{
    public AudioBackendException(string message) : base(message) { }

    public AudioBackendException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// One open audio stream. Part 2B defines the contract; Part 2C implements it over
/// WASAPI. Keeping the contract here means the DSP adapter, the ring buffer and the
/// device layer can all be finished and tested before a single sample moves.
/// </summary>
public interface IAudioStream : IDisposable
{
    AudioStreamState State { get; }

    /// <summary>The format actually negotiated with the device, which may not be the one requested.</summary>
    AudioStreamFormat Format { get; }

    /// <summary>The endpoint this stream is attached to.</summary>
    AudioDeviceInfo Device { get; }

    /// <summary>What sits in the signal path. Swapping it is safe while running.</summary>
    IAudioSampleProcessor Processor { get; set; }

    AudioStreamStatistics Statistics { get; }

    /// <summary>Raised when the stream stops on its own: device unplugged, format changed, service restarted.</summary>
    event EventHandler<AudioStreamFaultedEventArgs>? Faulted;

    void Start();

    void Stop();
}

public sealed class AudioStreamFaultedEventArgs : EventArgs
{
    public AudioStreamFaultedEventArgs(string reason, Exception? exception = null)
    {
        Reason = reason;
        Exception = exception;
    }

    public string Reason { get; }

    public Exception? Exception { get; }
}

/// <summary>Creates streams. Separated from the backend so Part 2C can supply a real one.</summary>
public interface IAudioStreamFactory
{
    IAudioStream Create(AudioStreamConfiguration configuration);
}
