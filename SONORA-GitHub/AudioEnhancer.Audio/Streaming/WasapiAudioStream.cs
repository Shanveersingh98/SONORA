using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using AudioEnhancer.Audio.Interop;

namespace AudioEnhancer.Audio;

/// <summary>
/// The real signal path:
///
///     Windows app mix -> Windows process-loopback capture -> AudioRingBuffer ->
///     AdaptiveResampler/RingFillController -> DSP -> WASAPI render -> output device
///
/// The normal device-loopback mode remains available for lower-level tests and explicit
/// capture-device scenarios. The application engine uses Windows process-loopback capture,
/// which is a user-mode Core Audio API path: it captures rendered application audio while
/// excluding the AudioEnhancer process tree. No virtual playback device, driver or Windows
/// default-endpoint change is needed for the live system-wide path.
///
/// <para><b>The two endpoints must be different.</b> Loopback captures whatever the
/// endpoint is playing, so rendering the enhanced audio back to the same endpoint feeds
/// it straight into the capture side and the signal runs away within a few buffers. The
/// stream refuses to open in that case with a message saying so, rather than letting it
/// happen into someone's headphones. The working arrangement is: Windows plays to
/// device A, this renders the enhanced result to device B, and you listen to B.</para>
///
/// <para>Threading: one engine thread owns open, recover and close. One capture thread
/// polls the loopback client. One render thread services the device event. All three are
/// MTA, so every COM call stays inside one apartment. Neither audio thread takes a lock,
/// allocates, or touches the UI - they talk to each other only through the lock-free
/// <see cref="AudioRingBuffer"/>.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioStream : IAudioStream
{
    private const int MaxRecoveryAttempts = 8;

    private readonly AudioStreamConfiguration _config;
    private readonly WindowsOutputDeviceManager _devices;
    private readonly object _lifecycleGate = new();

    private readonly ManualResetEventSlim _stopSignal = new(false);
    private readonly ManualResetEventSlim _faultSignal = new(false);

    private Thread? _engineThread;
    private volatile string _faultReason = "";
    private volatile bool _faultRecoverable;

    private volatile IAudioSampleProcessor _processor = PassthroughSampleProcessor.Instance;
    private volatile AudioStreamState _state = AudioStreamState.Closed;

    private AudioStreamFormat _format = AudioStreamFormat.DefaultStereo;
    private AudioDeviceInfo _device = new("unknown", "Not opened yet", false);

    private AudioRingBuffer? _ring;
    private long _framesProcessed;
    private volatile int _latencyFrames;
    private bool _disposed;

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uMilliseconds);

    public WasapiAudioStream(
        AudioStreamConfiguration configuration,
        WindowsOutputDeviceManager devices)
    {
        _config = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));

        if (_config.Direction != AudioStreamDirection.Loopback)
        {
            throw new AudioBackendException(
                $"This stream captures the system mix, so it only supports " +
                $"{nameof(AudioStreamDirection.Loopback)}. " +
                $"{_config.Direction} was requested.");
        }

        _devices.DefaultOutputChanged += OnDefaultOutputChanged;
    }

    // -----------------------------------------------------------------------
    //  IAudioStream
    // -----------------------------------------------------------------------

    public AudioStreamState State => _state;

    /// <summary>
    /// The render side's negotiated format: what the DSP engine is prepared with.
    /// </summary>
    public AudioStreamFormat Format => _format;

    /// <summary>
    /// The output endpoint the enhanced audio is played to.
    /// </summary>
    public AudioDeviceInfo Device => _device;

    /// <summary>
    /// The endpoint being captured: the one named by
    /// <see cref="AudioStreamConfiguration.CaptureDeviceId"/>,
    /// or the current default playback device when none was named.
    /// </summary>
    public AudioDeviceInfo CaptureDevice { get; private set; } =
        new("unknown", "Not opened yet", true);

    public IAudioSampleProcessor Processor
    {
        get => _processor;
        set => _processor = value ?? PassthroughSampleProcessor.Instance;
    }

    public AudioStreamStatistics Statistics
    {
        get
        {
            AudioRingBuffer? ring = _ring;

            return new AudioStreamStatistics(
                Interlocked.Read(ref _framesProcessed),
                ring?.OverrunSamples ?? 0,
                ring?.UnderrunSamples ?? 0,
                _latencyFrames + _processor.LatencyFrames);
        }
    }

    public event EventHandler<AudioStreamFaultedEventArgs>? Faulted;

    public void Start()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_engineThread is not null)
                return;

            _stopSignal.Reset();
            _faultSignal.Reset();
            _faultReason = "";
            _state = AudioStreamState.Opening;

            var thread = new Thread(EngineLoop)
            {
                Name = "AudioEnhancer engine",
                IsBackground = true,
            };

            thread.SetApartmentState(ApartmentState.MTA);

            _engineThread = thread;
            thread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;

        lock (_lifecycleGate)
        {
            thread = _engineThread;
            _engineThread = null;

            if (thread is null)
                return;

            _state = AudioStreamState.Stopping;
            _stopSignal.Set();
        }

        // Never called from an audio thread, so waiting here is safe.
        if (!thread.Join(TimeSpan.FromSeconds(5)))
            RaiseFaulted(
                "the audio threads did not shut down cleanly",
                null);

        _state = AudioStreamState.Closed;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _devices.DefaultOutputChanged -= OnDefaultOutputChanged;

        Stop();

        _stopSignal.Dispose();
        _faultSignal.Dispose();
    }

    // -----------------------------------------------------------------------
    //  Engine thread: open, run, recover, close
    // -----------------------------------------------------------------------

    private void EngineLoop()
    {
        int attempt = 0;
        bool faulted = false;

        while (!_stopSignal.IsSet)
        {
            Session? session = null;

            try
            {
                session = Session.Open(
                    _config,
                    _processor);

                _format = session.RenderFormat;
                _device = session.RenderDevice;
                CaptureDevice = session.CaptureDevice;
                _ring = session.Ring;
                _latencyFrames = session.LatencyFrames;

                _faultSignal.Reset();
                _faultReason = "";
                attempt = 0;
                _state = AudioStreamState.Running;

                // Step 7 diagnostic - see DiagnosticLog.
                // This is the one line that proves which two endpoints
                // were actually resolved and opened.
                DiagnosticLog.Write(
                    $"[CAPTURE] Started - capturing '{session.CaptureDevice.Name}' -> " +
                    $"rendering '{session.RenderDevice.Name}' " +
                    $"({session.CaptureFormat.SampleRate}Hz/" +
                    $"{session.CaptureFormat.Channels}ch -> " +
                    $"{session.RenderFormat.SampleRate}Hz/" +
                    $"{session.RenderFormat.Channels}ch)");

                RunSession(session);
            }
            catch (RecoverableStreamException ex)
            {
                SignalFault(
                    ex.Message,
                    recoverable: true);
            }
            catch (AudioBackendException ex)
            {
                SignalFault(
                    ex.Message,
                    recoverable: false);
            }
            catch (Exception ex)
            {
                SignalFault(
                    ex.Message,
                    recoverable: false);
            }
            finally
            {
                session?.Dispose();
                _ring = null;
            }

            if (_stopSignal.IsSet)
                break;

            string reason = _faultReason;
            bool recoverable = _faultRecoverable;

            RaiseFaulted(
                reason,
                null);

            if (!recoverable)
            {
                faulted = true;
                break;
            }

            attempt++;

            if (attempt > MaxRecoveryAttempts)
            {
                _faultReason =
                    $"giving up after {MaxRecoveryAttempts} attempts to reopen ({reason})";

                RaiseFaulted(
                    _faultReason,
                    null);

                faulted = true;
                break;
            }

            // Back off so a device that is still settling is not hammered.
            _state = AudioStreamState.Opening;

            int delayMs =
                Math.Min(
                    2000,
                    150 * (1 << Math.Min(attempt, 4)));

            if (_stopSignal.Wait(delayMs))
                break;
        }

        _state =
            faulted
                ? AudioStreamState.Faulted
                : AudioStreamState.Closed;

        // Let a later Start() create a fresh engine thread.
        // Stop() clears the field inside the lock before it joins,
        // so taking the lock here cannot deadlock.
        lock (_lifecycleGate)
        {
            if (ReferenceEquals(
                    _engineThread,
                    Thread.CurrentThread))
            {
                _engineThread = null;
            }
        }
    }

    /// <summary>
    /// Starts the two audio threads and blocks until something asks us to stop.
    /// The engine thread does the waiting so neither audio thread ever has to.
    /// </summary>
    private void RunSession(Session session)
    {
        TimeBeginPeriod(1);
        try
        {
            var captureThread = new Thread(
                () => CaptureLoop(session))
            {
                Name = "Sonora capture",
                IsBackground = true,
                Priority = ThreadPriority.Highest,
            };

            captureThread.SetApartmentState(
                ApartmentState.MTA);

            var renderThread = new Thread(
                () => RenderLoop(session))
            {
                Name = "Sonora render",
                IsBackground = true,
                Priority = ThreadPriority.Highest,
            };

            renderThread.SetApartmentState(
                ApartmentState.MTA);

            // Active only while a session is actually running.
            session.FillController.SetActive(true);

            captureThread.Start();
            renderThread.Start();

            WaitHandle.WaitAny(
                new[]
                {
                    _stopSignal.WaitHandle,
                    _faultSignal.WaitHandle
                });

            session.Stopping = true;
            session.RenderEvent.Set();

            captureThread.Join(
                TimeSpan.FromSeconds(2));

            renderThread.Join(
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            TimeEndPeriod(1);
        }
    }

    // -----------------------------------------------------------------------
    //  Capture thread
    // -----------------------------------------------------------------------

    /// <summary>
    /// Polls the loopback client and pushes frames into the ring buffer.
    /// </summary>
    private void CaptureLoop(Session session)
    {
        int pollMs =
            Math.Clamp(
                session.CapturePollMs,
                1,
                20);

        try
        {
            while (!session.Stopping &&
                   !_stopSignal.IsSet)
            {
                int hr =
                    session.CaptureClient.GetNextPacketSize(
                        out uint packetFrames);

                if (hr < 0)
                {
                    FaultFromHResult(
                        hr,
                        "capture");

                    return;
                }

                if (packetFrames == 0)
                {
                    Thread.Sleep(1);
                    continue;
                }

                while (packetFrames != 0 &&
                       !session.Stopping &&
                       !_stopSignal.IsSet)
                {
                    hr = session.CaptureClient.GetBuffer(
                        out IntPtr data,
                        out uint framesRead,
                        out uint flags,
                        out _,
                        out _);

                    if (hr == AudioClientHResult.AUDCLNT_S_BUFFER_EMPTY)
                        break;

                    if (hr < 0)
                    {
                        FaultFromHResult(
                            hr,
                            "capture");

                        return;
                    }

                    if (framesRead > 0)
                    {
                        bool silent =
                            (flags & AudioClientBufferFlags.Silent) != 0;

                        float peak =
                            PushCapturedFrames(
                                session,
                                data,
                                (int)framesRead,
                                silent);

                        LogCaptureProgress(
                            (int)framesRead,
                            peak);
                    }

                    hr =
                        session.CaptureClient.ReleaseBuffer(
                            framesRead);

                    if (hr < 0)
                    {
                        FaultFromHResult(
                            hr,
                            "capture");

                        return;
                    }

                    hr =
                        session.CaptureClient.GetNextPacketSize(
                            out packetFrames);

                    if (hr < 0)
                    {
                        FaultFromHResult(
                            hr,
                            "capture");

                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            SignalFault(
                $"capture thread stopped: {ex.Message}",
                recoverable: true);
        }
    }

    /// <summary>
    /// Converts one captured packet to float, maps it to the render channel layout
    /// and writes it to the ring.
    /// </summary>
    private static unsafe float PushCapturedFrames(
        Session session,
        IntPtr data,
        int frames,
        bool silent)
    {
        int captureChannels =
            session.CaptureFormat.Channels;

        int renderChannels =
            session.RenderFormat.Channels;

        int bytesPerFrame =
            session.CaptureFormat.BlockAlign;

        int offset = 0;
        float peak = 0.0f;

        while (offset < frames)
        {
            int chunk =
                Math.Min(
                    session.ScratchFrames,
                    frames - offset);

            int samples =
                chunk * captureChannels;

            Span<float> captured =
                session.CaptureScratch.AsSpan(
                    0,
                    samples);

            if (silent || data == IntPtr.Zero)
            {
                captured.Clear();
            }
            else
            {
                var bytes =
                    new ReadOnlySpan<byte>(
                        (void*)IntPtr.Add(
                            data,
                            offset * bytesPerFrame),
                        chunk * bytesPerFrame);

                SampleConverter.ToFloat(
                    bytes,
                    captured,
                    session.CaptureFormat);

                for (int i = 0; i < samples; i++)
                {
                    float abs = Math.Abs(captured[i]);
                    if (abs > peak)
                        peak = abs;
                }
            }

            Span<float> mapped =
                session.MapScratch.AsSpan(
                    0,
                    chunk * renderChannels);

            int mappedFrames =
                ChannelMapper.Map(
                    captured,
                    captureChannels,
                    mapped,
                    renderChannels);

            session.Ring.Write(
                mapped.Slice(
                    0,
                    mappedFrames * renderChannels));

            offset += chunk;
        }

        return peak;
    }

    // -----------------------------------------------------------------------
    //  Render thread
    // -----------------------------------------------------------------------

    /// <summary>
    /// Waits for the device to ask for audio, pulls that many frames out of the ring,
    /// runs them through the DSP processor and hands them to WASAPI.
    /// </summary>
    private void RenderLoop(Session session)
    {
        int renderChannels =
            session.RenderFormat.Channels;

        int timeoutMs =
            Math.Max(
                4,
                (int)session.RenderFormat
                    .DurationOf(session.RenderBufferFrames)
                    .TotalMilliseconds * 4);

        int preRollCycles = 0;
        try
        {
            while (!session.Stopping &&
                   !_stopSignal.IsSet)
            {
                if (!session.RenderEvent.WaitOne(timeoutMs))
                    continue;

                if (session.Stopping ||
                    _stopSignal.IsSet)
                {
                    break;
                }

                int hr =
                    session.RenderClient_GetPadding(
                        out uint padding);

                if (hr < 0)
                {
                    FaultFromHResult(
                        hr,
                        "render");

                    return;
                }

                int framesAvailable =
                    session.RenderBufferFrames -
                    (int)padding;

                if (framesAvailable <= 0)
                    continue;

                hr =
                    session.RenderClient.GetBuffer(
                        (uint)framesAvailable,
                        out IntPtr buffer);

                if (hr < 0)
                {
                    FaultFromHResult(
                        hr,
                        "render");

                    return;
                }

                int samples =
                    framesAvailable * renderChannels;

                Span<float> block =
                    session.RenderScratch.AsSpan(
                        0,
                        samples);

                // Pre-roll priming: on initial callbacks (up to 3 cycles / 30ms), if capture has not
                // yet filled the target cushion, output silence to build a safety buffer so playback never starves
                if (preRollCycles < 3 && session.Ring.Count < session.FillController.TargetFillSamples / 2)
                {
                    preRollCycles++;
                    block.Clear();
                    _processor.Process(block);
                    WriteRenderBuffer(session, buffer, block);
                    session.RenderClient.ReleaseBuffer((uint)framesAvailable, 0);
                    Interlocked.Add(ref _framesProcessed, framesAvailable);
                    continue;
                }

                // RingBuffer -> AdaptiveResampler/RingFillController
                // -> existing DSP processor.
                session.Resampler.Render(
                    session.Ring,
                    block);

                session.FillController.Update(
                    session.Ring.Count);

                // Anti-Flutter Slew & Deadband: when nominal sample rates match (e.g. 48kHz capture -> 48kHz render),
                // lock to bit-perfect 1.0 within normal buffer fluctuations (25% - 75% capacity).
                // Apply gentle slew-rate limiting so any ratio adjustments are completely smooth and imperceptible,
                // eliminating any vibrato, tremolo, pitch warble, or "shaky" audio artifacts.
                double targetRatio;
                if (Math.Abs(session.NominalResampleRatio - 1.0) < 1e-6)
                {
                    int ringCount = session.Ring.Count;
                    int capacity = session.Ring.Capacity;
                    if (ringCount > capacity * 0.25 && ringCount < capacity * 0.75)
                    {
                        targetRatio = 1.0;
                    }
                    else
                    {
                        targetRatio = 1.0 + session.FillController.Correction;
                    }
                }
                else
                {
                    targetRatio =
                        session.NominalResampleRatio *
                        (1.0 + session.FillController.Correction);
                }

                const double maxRatioStep = 0.00001;
                double currentRatio = session.Resampler.Ratio;
                if (targetRatio > currentRatio + maxRatioStep)
                    session.Resampler.Ratio = currentRatio + maxRatioStep;
                else if (targetRatio < currentRatio - maxRatioStep)
                    session.Resampler.Ratio = currentRatio - maxRatioStep;
                else
                    session.Resampler.Ratio = targetRatio;

                IAudioSampleProcessor processor =
                    _processor;

                processor.Process(block);

                WriteRenderBuffer(
                    session,
                    buffer,
                    block);

                hr =
                    session.RenderClient.ReleaseBuffer(
                        (uint)framesAvailable,
                        0);

                if (hr < 0)
                {
                    FaultFromHResult(
                        hr,
                        "render");

                    return;
                }

                long totalRendered =
                    Interlocked.Add(
                        ref _framesProcessed,
                        framesAvailable);

                LogRenderProgress(
                    totalRendered,
                    framesAvailable);
            }
        }
        catch (Exception ex)
        {
            SignalFault(
                $"render thread stopped: {ex.Message}",
                recoverable: true);
        }
    }

    // Step 7 diagnostic
    private const long DiagnosticLogIntervalFrames = 48_000;

    private long _diagnosticFramesCaptured;
    private float _diagnosticPeakCaptureLevel;

    private void LogCaptureProgress(
        int framesJustCaptured,
        float blockPeak)
    {
        if (!DiagnosticLog.Enabled)
            return;

        if (blockPeak > _diagnosticPeakCaptureLevel)
            _diagnosticPeakCaptureLevel = blockPeak;

        long total =
            Interlocked.Add(
                ref _diagnosticFramesCaptured,
                framesJustCaptured);

        long previous =
            total - framesJustCaptured;

        if (total / DiagnosticLogIntervalFrames !=
            previous / DiagnosticLogIntervalFrames)
        {
            float peak = _diagnosticPeakCaptureLevel;
            _diagnosticPeakCaptureLevel = 0.0f;
            DiagnosticLog.Write(
                $"[CAPTURE] Samples received: {total}, Peak level: {peak:F4}");
        }
    }

    private void LogRenderProgress(
        long totalRendered,
        int framesJustRendered)
    {
        if (!DiagnosticLog.Enabled)
            return;

        long previous =
            totalRendered - framesJustRendered;

        if (totalRendered / DiagnosticLogIntervalFrames !=
            previous / DiagnosticLogIntervalFrames)
        {
            DiagnosticLog.Write(
                $"[DSP] Processed samples: {totalRendered}");

            DiagnosticLog.Write(
                $"[RENDER] Samples written: {totalRendered}");
        }
    }

    private static unsafe void WriteRenderBuffer(
        Session session,
        IntPtr buffer,
        ReadOnlySpan<float> block)
    {
        if (buffer == IntPtr.Zero)
            return;

        int byteCount =
            block.Length *
            session.RenderFormat.BytesPerSample;

        var destination =
            new Span<byte>(
                (void*)buffer,
                byteCount);

        SampleConverter.FromFloat(
            block,
            destination,
            session.RenderFormat);
    }

    // -----------------------------------------------------------------------
    //  Faults
    // -----------------------------------------------------------------------

    private void OnDefaultOutputChanged(
        object? sender,
        EventArgs e)
    {
        if (_state is not
            (AudioStreamState.Running or
             AudioStreamState.Open))
        {
            return;
        }

        // During AudioEnhancer routing, both endpoints are explicitly pinned:
        // CaptureDeviceId = AudioEnhancer Virtual Output
        // DeviceId        = the real physical output device.
        //
        // Therefore a Windows default-device notification is intentional and
        // must not interrupt the running stream.
        if (!string.IsNullOrEmpty(
                _config.CaptureDeviceId) &&
            !string.IsNullOrEmpty(
                _config.DeviceId))
        {
            return;
        }

        bool follow =
            _config.FollowDefaultDevice ||
            !string.IsNullOrEmpty(
                _config.DeviceId);

        SignalFault(
            "the default playback device changed",
            recoverable: follow);
    }

    private void FaultFromHResult(
        int hr,
        string side)
    {
        SignalFault(
            $"{side} stopped: {AudioClientHResult.Describe(hr)}",
            AudioClientHResult.IsRecoverable(hr));
    }

    /// <summary>
    /// Safe to call from an audio thread.
    /// </summary>
    private void SignalFault(
        string reason,
        bool recoverable)
    {
        if (_faultSignal.IsSet)
            return;

        _faultReason = reason;
        _faultRecoverable = recoverable;
        _faultSignal.Set();
    }

    private void RaiseFaulted(
        string reason,
        Exception? exception)
    {
        if (string.IsNullOrEmpty(reason))
            return;

        try
        {
            Faulted?.Invoke(
                this,
                new AudioStreamFaultedEventArgs(
                    reason,
                    exception));
        }
        catch
        {
            // A subscriber throwing must not take the engine thread down.
        }
    }

    /// <summary>
    /// A fault the stream expects to survive by reopening.
    /// </summary>
    private sealed class RecoverableStreamException : Exception
    {
        public RecoverableStreamException(
            string message)
            : base(message)
        {
        }
    }

    // -----------------------------------------------------------------------
    //  One open pair of endpoints and everything sized for them
    // -----------------------------------------------------------------------

    private sealed class Session : IDisposable
    {
        private IMMDevice? _captureDevice;
        private IMMDevice? _renderDevice;

        private IAudioClient? _captureClient;
        private IAudioClient? _renderClient;

        private object? _captureService;
        private object? _renderService;

        private bool _started;

        private Session()
        {
            RenderEvent =
                new AutoResetEvent(false);

            CaptureEvent =
                new AutoResetEvent(false);
        }

        public volatile bool Stopping;

        public AutoResetEvent RenderEvent { get; }

        public AutoResetEvent CaptureEvent { get; }

        public AudioStreamFormat CaptureFormat { get; private set; } =
            AudioStreamFormat.DefaultStereo;

        public AudioStreamFormat RenderFormat { get; private set; } =
            AudioStreamFormat.DefaultStereo;

        public AudioDeviceInfo CaptureDevice { get; private set; } =
            new("unknown", "Unknown", true);

        public AudioDeviceInfo RenderDevice { get; private set; } =
            new("unknown", "Unknown", false);

        public IAudioCaptureClient CaptureClient { get; private set; } = null!;

        public IAudioRenderClient RenderClient { get; private set; } = null!;

        public int CaptureBufferFrames { get; private set; }

        public int RenderBufferFrames { get; private set; }

        public int ScratchFrames { get; private set; }

        public int CapturePollMs { get; private set; } = 5;

        public int LatencyFrames { get; private set; }

        public float[] CaptureScratch { get; private set; } =
            Array.Empty<float>();

        public float[] MapScratch { get; private set; } =
            Array.Empty<float>();

        public float[] RenderScratch { get; private set; } =
            Array.Empty<float>();

        public AudioRingBuffer Ring { get; private set; } =
            new(2);

        public AdaptiveResampler Resampler { get; } =
            new();

        public RingFillController FillController { get; private set; } = null!;

        public double NominalResampleRatio { get; private set; } =
            1.0;

        public int RenderClient_GetPadding(
            out uint padding) =>
            _renderClient!.GetCurrentPadding(
                out padding);

        public static Session Open(
            AudioStreamConfiguration config,
            IAudioSampleProcessor processor)
        {
            var session = new Session();

            IMMDeviceEnumerator? enumerator =
                null;

            try
            {
                enumerator =
                    (IMMDeviceEnumerator)
                    new MMDeviceEnumeratorComObject();

                bool useProcessLoopback =
                    config.UseProcessLoopbackCapture;

                bool captureIsDefault =
                    string.IsNullOrEmpty(
                        config.CaptureDeviceId);

                IMMDevice? captureDevice = null;

                if (!useProcessLoopback)
                {
                    if (captureIsDefault)
                    {
                        Check(
                            enumerator.GetDefaultAudioEndpoint(
                                EDataFlow.Render,
                                ERole.Console,
                                out captureDevice),
                            "find the default playback device");

                        if (captureDevice is null)
                        {
                            throw new RecoverableStreamException(
                                "there is no default playback device to capture from");
                        }
                    }
                    else
                    {
                        Check(
                            enumerator.GetDevice(
                                config.CaptureDeviceId!,
                                out captureDevice),
                            "open the chosen capture device");

                        if (captureDevice is null)
                        {
                            throw new RecoverableStreamException(
                                "the chosen capture device could not be opened");
                        }
                    }

                    session._captureDevice =
                        captureDevice;
                }

                IMMDevice? renderDevice;

                if (string.IsNullOrEmpty(
                        config.DeviceId))
                {
                    Check(
                        enumerator.GetDefaultAudioEndpoint(
                            EDataFlow.Render,
                            ERole.Multimedia,
                            out renderDevice),
                        "find the default output device");
                }
                else
                {
                    int hr = enumerator.GetDevice(config.DeviceId, out renderDevice);
                    if (hr == unchecked((int)0x80070490) || hr == AudioClientHResult.AUDCLNT_E_DEVICE_INVALIDATED)
                    {
                        throw new RecoverableStreamException(
                            $"the output device '{config.DeviceId}' was disconnected or removed");
                    }
                    Check(hr, "open the chosen output device");
                }

                if (renderDevice is null)
                {
                    throw new RecoverableStreamException(
                        "the output device could not be opened");
                }

                session._renderDevice =
                    renderDevice;

                Check(
                    renderDevice.GetId(
                        out string? renderId),
                    "read the output device id");

                if (useProcessLoopback)
                {
                    uint targetProcessId =
                        config.ProcessLoopbackTargetProcessId != 0
                            ? config.ProcessLoopbackTargetProcessId
                            : (uint)Environment.ProcessId;

                    session.CaptureDevice =
                        new AudioDeviceInfo(
                            ProcessLoopbackAudioClient.SyntheticDeviceId,
                            ProcessLoopbackAudioClient.SyntheticDeviceName,
                            true);

                    session.RenderDevice =
                        WindowsOutputDeviceManager.Describe(
                            renderDevice,
                            isDefault: renderId == null)
                        ?? new AudioDeviceInfo(
                            renderId ?? "unknown",
                            "Output device",
                            false);

                    session._captureClient =
                        ProcessLoopbackAudioClient
                            .ActivateExcludingProcessTree(
                                targetProcessId);
                }
                else
                {
                    Check(
                        captureDevice!.GetId(
                            out string? captureId),
                        "read the capture device id");

                    session.CaptureDevice =
                        WindowsOutputDeviceManager.Describe(
                            captureDevice,
                            isDefault: captureIsDefault)
                        ?? new AudioDeviceInfo(
                            captureId ?? "unknown",
                            captureIsDefault
                                ? "Default playback device"
                                : "Capture device",
                            captureIsDefault);

                    session.RenderDevice =
                        WindowsOutputDeviceManager.Describe(
                            renderDevice,
                            isDefault: renderId == captureId)
                        ?? new AudioDeviceInfo(
                            renderId ?? "unknown",
                            "Output device",
                            false);

                    if (string.Equals(
                            captureId,
                            renderId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new AudioBackendException(
                            $"The output device is the same as the device being captured " +
                            $"('{session.RenderDevice.Name}'). " +
                            "Loopback captures whatever that device is playing, so sending " +
                            "the enhanced audio back to it would feed straight into the capture " +
                            "side and run away. Choose a different output device - let Windows " +
                            "play to one device and have the enhancer render to the one you are listening on.");
                    }

                    session._captureClient =
                        Activate(
                            captureDevice!,
                            "capture");
                }

                session._renderClient =
                    Activate(
                        renderDevice,
                        "output");

                long bufferTicks =
                    config.EffectiveBufferDuration.Ticks;

                session.RenderFormat =
                    InitialiseRender(
                        session._renderClient!,
                        bufferTicks,
                        session.RenderEvent);

                session.CaptureFormat =
                    useProcessLoopback
                        ? InitialiseProcessLoopbackCapture(
                            session._captureClient!,
                            session.RenderFormat,
                            bufferTicks,
                            session.CaptureEvent)
                        : InitialiseCapture(
                            session._captureClient!,
                            bufferTicks);

                Check(
                    session._renderClient!.GetBufferSize(
                        out uint renderFrames),
                    "read the output buffer size");

                session.RenderBufferFrames =
                    (int)renderFrames;

                if (useProcessLoopback)
                {
                    session.CaptureBufferFrames =
                        Math.Max(
                            480,
                            Math.Min(
                                4096,
                                session.RenderBufferFrames));

                    session.CapturePollMs = 5;
                }
                else
                {
                    Check(
                        session._captureClient!.GetBufferSize(
                            out uint captureFrames),
                        "read the capture buffer size");

                    session.CaptureBufferFrames =
                        (int)captureFrames;

                    Check(
                        session._captureClient!.GetDevicePeriod(
                            out long capturePeriod,
                            out _),
                        "read the capture period");

                    session.CapturePollMs =
                        Math.Max(
                            1,
                            (int)(
                                new TimeSpan(
                                    capturePeriod)
                                .TotalMilliseconds / 2));
                }

                session.ScratchFrames =
                    Math.Max(
                        session.CaptureBufferFrames,
                        session.RenderBufferFrames);

                session.CaptureScratch =
                    new float[
                        session.ScratchFrames *
                        session.CaptureFormat.Channels];

                session.MapScratch =
                    new float[
                        session.ScratchFrames *
                        session.RenderFormat.Channels];

                session.RenderScratch =
                    new float[
                        session.RenderBufferFrames *
                        session.RenderFormat.Channels];

                session.Ring =
                    new AudioRingBuffer(
                        config.RingBufferSamples(
                            session.RenderFormat));

                session.NominalResampleRatio =
                    (double)
                    session.CaptureFormat.SampleRate /
                    session.RenderFormat.SampleRate;

                session.Resampler.Prepare(
                    session.RenderFormat.Channels,
                    session.CaptureFormat.SampleRate,
                    session.RenderFormat.SampleRate);

                session.FillController =
                    new RingFillController(
                        session.Ring.Capacity / 2);
                session.FillController.SetActive(true);

                session.CaptureClient =
                    GetService<IAudioCaptureClient>(
                        session._captureClient!,
                        NativeMethods.IID_IAudioCaptureClient,
                        out session._captureService,
                        "capture client");

                session.RenderClient =
                    GetService<IAudioRenderClient>(
                        session._renderClient!,
                        NativeMethods.IID_IAudioRenderClient,
                        out session._renderService,
                        "render client");

                processor.Prepare(
                    session.RenderFormat);

                processor.Reset();

                session.LatencyFrames =
                    session.CaptureBufferFrames +
                    session.RenderBufferFrames;

                PrimeWithSilence(session);

                Check(
                    session._captureClient!.Start(),
                    "start capture");

                Check(
                    session._renderClient!.Start(),
                    "start playback");

                session._started = true;

                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
            finally
            {
                ComHelpers.Release(
                    enumerator);
            }
        }

        private static IAudioClient Activate(
            IMMDevice device,
            string what)
        {
            Guid iid =
                NativeMethods.IID_IAudioClient;

            Check(
                device.Activate(
                    ref iid,
                    ClsCtx.All,
                    IntPtr.Zero,
                    out object? instance),
                $"activate the {what} client");

            if (instance is not IAudioClient client)
            {
                throw new RecoverableStreamException(
                    $"the {what} device did not provide an audio client");
            }

            return client;
        }

        private static AudioStreamFormat InitialiseCapture(
            IAudioClient client,
            long bufferTicks)
        {
            IntPtr formatPointer =
                IntPtr.Zero;

            try
            {
                Check(
                    client.GetMixFormat(
                        out formatPointer),
                    "read the capture mix format");

                AudioStreamFormat format =
                    AudioStreamFormat.FromWaveFormat(
                        formatPointer)
                    ?? throw new AudioBackendException(
                        "the capture device reported a format this build cannot read");

                if (!format.IsValid)
                {
                    throw new AudioBackendException(
                        $"the capture device reported an unusable format ({format})");
                }

                Check(
                    client.Initialize(
                        AudioClientShareMode.Shared,
                        AudioClientStreamFlags.Loopback |
                        AudioClientStreamFlags.AutoConvertPcm,
                        bufferTicks,
                        0,
                        formatPointer,
                        IntPtr.Zero),
                    "open loopback capture");

                return format;
            }
            finally
            {
                if (formatPointer != IntPtr.Zero)
                {
                    NativeMethods.CoTaskMemFree(
                        formatPointer);
                }
            }
        }

        private static AudioStreamFormat InitialiseProcessLoopbackCapture(
            IAudioClient client,
            AudioStreamFormat renderFormat,
            long bufferTicks,
            AutoResetEvent captureEvent)
        {
            if (!renderFormat.IsValid)
            {
                throw new AudioBackendException(
                    $"the output device reported an unusable format ({renderFormat})");
            }

            WaveFormatEx requested = new()
            {
                FormatTag =
                    renderFormat.SampleType == SampleFormat.Float32
                        ? WaveFormatTag.IeeeFloat
                        : WaveFormatTag.Pcm,

                Channels =
                    checked((ushort)renderFormat.Channels),

                SamplesPerSec =
                    checked((uint)renderFormat.SampleRate),

                BitsPerSample =
                    checked((ushort)renderFormat.BitsPerSample),

                BlockAlign =
                    checked((ushort)renderFormat.BlockAlign),

                AvgBytesPerSec =
                    checked((uint)renderFormat.BytesPerSecond),

                ExtraSize = 0,
            };

            IntPtr formatPointer =
                Marshal.AllocCoTaskMem(
                    Marshal.SizeOf<WaveFormatEx>());

            try
            {
                Marshal.StructureToPtr(
                    requested,
                    formatPointer,
                    fDeleteOld: false);

                Check(
                    client.Initialize(
                        AudioClientShareMode.Shared,
                        AudioClientStreamFlags.Loopback |
                        AudioClientStreamFlags.EventCallback |
                        AudioClientStreamFlags.AutoConvertPcm,
                        bufferTicks,
                        0,
                        formatPointer,
                        IntPtr.Zero),
                    "open process-loopback capture");

                Check(
                    client.SetEventHandle(
                        captureEvent.SafeWaitHandle
                            .DangerousGetHandle()),
                    "attach the process-loopback capture event");

                return renderFormat;
            }
            finally
            {
                Marshal.FreeCoTaskMem(
                    formatPointer);
            }
        }

        private static AudioStreamFormat InitialiseRender(
            IAudioClient client,
            long bufferTicks,
            AutoResetEvent renderEvent)
        {
            IntPtr formatPointer =
                IntPtr.Zero;

            try
            {
                Check(
                    client.GetMixFormat(
                        out formatPointer),
                    "read the output mix format");

                AudioStreamFormat format =
                    AudioStreamFormat.FromWaveFormat(
                        formatPointer)
                    ?? throw new AudioBackendException(
                        "the output device reported a format this build cannot read");

                if (!format.IsValid)
                {
                    throw new AudioBackendException(
                        $"the output device reported an unusable format ({format})");
                }

                Check(
                    client.Initialize(
                        AudioClientShareMode.Shared,
                        AudioClientStreamFlags.EventCallback |
                        AudioClientStreamFlags.NoPersist |
                        AudioClientStreamFlags.AutoConvertPcm,
                        bufferTicks,
                        0,
                        formatPointer,
                        IntPtr.Zero),
                    "open the output stream");

                Check(
                    client.SetEventHandle(
                        renderEvent.SafeWaitHandle
                            .DangerousGetHandle()),
                    "attach the playback event");

                return format;
            }
            finally
            {
                if (formatPointer != IntPtr.Zero)
                {
                    NativeMethods.CoTaskMemFree(
                        formatPointer);
                }
            }
        }

        private static T GetService<T>(
            IAudioClient client,
            Guid iid,
            out object? keepAlive,
            string what)
            where T : class
        {
            Guid serviceId = iid;

            Check(
                client.GetService(
                    ref serviceId,
                    out object? instance),
                $"obtain the {what}");

            keepAlive = instance;

            if (instance is not T service)
            {
                throw new RecoverableStreamException(
                    $"the device did not provide a {what}");
            }

            return service;
        }

        private static void PrimeWithSilence(
            Session session)
        {
            int hr =
                session.RenderClient.GetBuffer(
                    (uint)session.RenderBufferFrames,
                    out IntPtr buffer);

            if (hr < 0 ||
                buffer == IntPtr.Zero)
            {
                return;
            }

            session.RenderClient.ReleaseBuffer(
                (uint)session.RenderBufferFrames,
                AudioClientBufferFlags.Silent);
        }

        private static void Check(
            int hr,
            string what)
        {
            if (hr >= 0)
                return;

            string message =
                $"could not {what}: {AudioClientHResult.Describe(hr)}";

            if (AudioClientHResult.IsRecoverable(hr))
            {
                throw new RecoverableStreamException(
                    message);
            }

            throw new AudioBackendException(
                message);
        }

        public void Dispose()
        {
            Stopping = true;

            if (_started)
            {
                try
                {
                    _captureClient?.Stop();
                }
                catch
                {
                }

                try
                {
                    _renderClient?.Stop();
                }
                catch
                {
                }

                _started = false;
            }

            ComHelpers.Release(_captureService);
            ComHelpers.Release(_renderService);
            ComHelpers.Release(_captureClient);
            ComHelpers.Release(_renderClient);
            ComHelpers.Release(_captureDevice);
            ComHelpers.Release(_renderDevice);

            _captureService = null;
            _renderService = null;
            _captureClient = null;
            _renderClient = null;
            _captureDevice = null;
            _renderDevice = null;

            RenderEvent.Dispose();
            CaptureEvent.Dispose();
        }
    }
}