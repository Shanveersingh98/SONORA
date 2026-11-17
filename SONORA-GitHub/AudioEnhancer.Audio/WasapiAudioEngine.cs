using System.Linq;
using System.Runtime.Versioning;
using AudioEnhancer.Core;

namespace AudioEnhancer.Audio;

/// <summary>
/// The real audio engine.
///
/// System-wide routing model:
/// Windows applications
///     -> AudioEnhancer Virtual Output
///     -> WASAPI loopback capture
///     -> existing DSP processor
///     -> physical playback device
///
/// The Windows default playback roles are changed only while enhancement is running
/// and are restored when enhancement stops or the engine is disposed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioEngine : IAudioEngine
{
    private readonly IAudioBackend _backend;
    private readonly DspEngineSampleProcessor _processor = new();
    private readonly object _gate = new();
    private readonly object _streamLifecycleGate = new();
    private readonly object _debounceGate = new();
    private CancellationTokenSource? _migrationCts;

    private readonly IDefaultAudioEndpointRouter _defaultEndpointRouter =
        new WindowsDefaultAudioEndpointRouter();

    private IAudioStream? _stream;
    private DefaultAudioEndpointSnapshot? _routingSnapshot;

    private EngineState _state = EngineState.Stopped;
    private string _statusMessage = "Ready";
    private EnhancerSettings? _pendingSettings;

    private string? _outputDeviceId;
    private string? _captureDeviceId;
    private float? _originalPhysicalVolume;
    private string? _activePhysicalOutputId;
    private Streaming.WasapiSilenceKeepAlive? _keepAlive;

    private bool _disposed;
    private bool _stateDirty;

    public WasapiAudioEngine(IAudioBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));

        if (!_backend.IsAvailable)
        {
            _state = EngineState.NotAvailable;
            _statusMessage = _backend.Description;
        }

        // Seamless dynamic output migration (e.g. plugging in or unplugging headphones while audio is playing)
        try
        {
            _backend.Devices.DefaultOutputChanged += OnPhysicalDefaultOutputChanged;
            _backend.Devices.DevicesChanged += OnPhysicalDevicesChanged;
        }
        catch { }

        // Safeguard: Ensure Windows default playback endpoint is restored when process exits
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    public EngineState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public string StatusMessage
    {
        get
        {
            lock (_gate)
                return _statusMessage;
        }
    }

    public event EventHandler? StateChanged;

    /// <summary>
    /// When true, audio passes through untouched (DSP bypassed).
    /// Used when enhancement is switched OFF while keeping the audio stream alive.
    /// </summary>
    public bool Bypass
    {
        get => _processor.Bypass;
        set
        {
            _processor.Bypass = value;
            DiagnosticLog.Write($"[ENGINE] Bypass set to: {value}");
        }
    }

    /// <summary>
    /// Existing DSP chain.
    /// </summary>
    public DspEngineSampleProcessor Processor => _processor;

    /// <summary>
    /// Statistics from the active WASAPI stream.
    /// </summary>
    public AudioStreamStatistics? Statistics
    {
        get
        {
            lock (_gate)
                return _stream?.Statistics;
        }
    }

    /// <summary>
    /// Reports the currently detected AudioEnhancer virtual endpoint.
    /// </summary>
    public VirtualEndpointStatusReport GetVirtualEndpointStatus() =>
        VirtualEndpointStatusEvaluator.Evaluate(_backend.Devices);

    /// <summary>
    /// Existing public hook for selecting the virtual endpoint.
    /// Kept for compatibility/tests.
    /// </summary>
    public VirtualEndpointCaptureOutcome TryUseVirtualEndpointForCapture() =>
        VirtualEndpointCapture.TrySelect(this, _backend.Devices);

    /// <summary>
    /// Optional explicit physical output selection.
    /// The automatic system-wide path normally uses the physical device that was
    /// the Windows default when enhancement started.
    /// </summary>
    public void SetOutputDevice(string? deviceId)
    {
        lock (_gate)
            _outputDeviceId = deviceId;

        if (State == EngineState.Running)
        {
            string? targetId = ResolveBestPhysicalOutputId();
            if (!string.IsNullOrEmpty(targetId))
            {
                SwitchPhysicalOutput(targetId);
            }
        }
    }

    /// <summary>
    /// Existing capture-device setter.
    /// The automatic virtual-endpoint path sets this internally to the virtual
    /// AudioEnhancer endpoint when Start() begins.
    /// </summary>
    public void SetCaptureDevice(string? deviceId)
    {
        lock (_gate)
            _captureDeviceId = deviceId;
    }

    public void Start()
    {
        lock (_streamLifecycleGate)
        {
            DiagnosticLog.Write("[ENGINE] Start");

            IAudioStream? stream = null;
            EnhancerSettings? settings;

            lock (_gate)
            {
                if (_disposed || _stream is not null)
                    return;

                if (!_backend.IsAvailable)
                {
                    SetStateLocked(
                        EngineState.NotAvailable,
                        _backend.Description);

                    RaiseStateChanged();
                    return;
                }

                SetStateLocked(
                    EngineState.Starting,
                    "Starting audio...");

                settings = _pendingSettings;
            }

            DefaultAudioEndpointSnapshot? routingSnapshot = null;

            try
            {
                // -------------------------------------------------------------
                // 1. Ensure any installed virtual playback endpoint is visible.
                // -------------------------------------------------------------
                try
                {
                    WindowsDefaultAudioEndpointRouter.EnsureAllVirtualEndpointsVisible();
                    (_backend.Devices as IOutputDeviceProbe)?.Refresh();
                }
                catch { }

                VirtualEndpointStatusReport virtualStatus =
                    GetVirtualEndpointStatus();

                if (virtualStatus.IsInstalled && virtualStatus.Device is not null)
                {
                    string virtualDeviceId = virtualStatus.Device.Id;
                    bool isRealWindowsEndpoint = virtualDeviceId.StartsWith("{", StringComparison.Ordinal);

                    if (isRealWindowsEndpoint)
                    {
                        // -------------------------------------------------------------
                        // 2. Save the user's current Windows playback defaults.
                        // -------------------------------------------------------------
                        if (_defaultEndpointRouter.TryCapture(
                                out DefaultAudioEndpointSnapshot snapshot,
                                out string routingError))
                        {
                            routingSnapshot = snapshot;

                            // -------------------------------------------------------------
                            // 3. Decide where enhanced audio will physically be played.
                            //
                            // Normally this is the physical device that was Windows'
                            // Multimedia default before we temporarily changed it.
                            // -------------------------------------------------------------
                            string? physicalOutputId =
                                ResolveBestPhysicalOutputId()
                                ?? _outputDeviceId
                                ?? snapshot.MultimediaDeviceId
                                ?? snapshot.ConsoleDeviceId
                                ?? snapshot.CommunicationsDeviceId;

                            if (string.IsNullOrWhiteSpace(physicalOutputId))
                            {
                                throw new AudioBackendException(
                                    "Windows did not provide a physical playback endpoint.");
                            }

                            // Never render to our own virtual endpoint.
                            if (string.Equals(
                                    physicalOutputId,
                                    virtualDeviceId,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                var fallbackDevice = _backend.Devices.GetOutputDevices()
                                    .FirstOrDefault(d => !VirtualEndpointDetector.IsVirtualEndpoint(d));
                                if (fallbackDevice != null)
                                {
                                    physicalOutputId = fallbackDevice.Id;
                                }
                                else
                                {
                                    throw new AudioBackendException(
                                        "The physical output endpoint is the same as the AudioEnhancer virtual endpoint.");
                                }
                            }

                            // -------------------------------------------------------------
                            // 4. Capture original physical volume for restoration upon exit.
                            // -------------------------------------------------------------
                            float? physicalVolume = WindowsDefaultAudioEndpointRouter.GetEndpointVolumeScalar(physicalOutputId);
                            if (physicalVolume.HasValue && physicalVolume.Value >= 0.99f)
                            {
                                float? virtVol = WindowsDefaultAudioEndpointRouter.GetEndpointVolumeScalar(virtualDeviceId);
                                if (virtVol.HasValue && virtVol.Value > 0.01f && virtVol.Value < 0.95f)
                                {
                                    physicalVolume = virtVol.Value;
                                }
                            }
                            _originalPhysicalVolume = physicalVolume;
                            _activePhysicalOutputId = physicalOutputId;

                            // -------------------------------------------------------------
                            // 5. Temporarily make AudioEnhancer Virtual Output the Windows
                            //    default for Console, Multimedia and Communications roles.
                            // -------------------------------------------------------------
                            if (!_defaultEndpointRouter.TrySetAllRoles(
                                    virtualDeviceId,
                                    out routingError))
                            {
                                throw new AudioBackendException(routingError);
                            }

                            // -------------------------------------------------------------
                            // 6. Post-switch volume synchronization:
                            //    Set the virtual device volume AFTER the role switch so Windows
                            //    taskbar volume stays at the user's true volume (e.g. 56%).
                            //    Then un-throttle the physical output to 1.0 (100%) so physical
                            //    laptop speakers can deliver their absolute maximum volume!
                            // -------------------------------------------------------------
                            if (physicalVolume.HasValue)
                            {
                                // Allow Windows MMDevice audio policy 60ms to complete default endpoint session
                                // registration before applying the volume, preventing Windows from defaulting to 100%.
                                Thread.Sleep(60);
                                WindowsDefaultAudioEndpointRouter.SetEndpointVolumeScalar(virtualDeviceId, physicalVolume.Value);
                                DiagnosticLog.Write($"[ENGINE] Set Virtual Endpoint volume post-switch to: {physicalVolume.Value * 100:0}%");

                                ConfigurePhysicalVolume(physicalOutputId, physicalVolume.Value);
                            }

                            // Start continuous silence keep-alive so virtual endpoint pin never sleeps or faults during video transitions/ads
                            try
                            {
                                _keepAlive?.Dispose();
                                _keepAlive = new Streaming.WasapiSilenceKeepAlive(virtualDeviceId);
                                _keepAlive.Start();
                            }
                            catch (Exception ex)
                            {
                                DiagnosticLog.Write($"[ENGINE] Silence keep-alive failed: {ex.Message}");
                            }

                            // -------------------------------------------------------------
                            // 5. Capture the virtual endpoint with normal WASAPI loopback.
                            //
                            // IMPORTANT:
                            // Process-loopback is deliberately disabled.
                            // This is the new system-wide virtual-device architecture.
                            // -------------------------------------------------------------
                            var configuration = new AudioStreamConfiguration(
                                DeviceId: physicalOutputId,
                                Direction: AudioStreamDirection.Loopback,
                                BufferDuration: TimeSpan.FromMilliseconds(20),
                                UseEventCallback: true,
                                FollowDefaultDevice: false,
                                CaptureDeviceId: virtualDeviceId)
                            {
                                UseProcessLoopbackCapture = false
                            };

                            stream = _backend.Create(configuration);

                            lock (_gate)
                            {
                                _captureDeviceId = virtualDeviceId;
                                _stream = stream;
                                _routingSnapshot = routingSnapshot;
                            }
                        }
                    }
                    else
                    {
                        // Non-real mock endpoint (e.g. in test harness)
                        bool useExplicitVirtual = !string.IsNullOrEmpty(_captureDeviceId);
                        var configuration = new AudioStreamConfiguration(
                            DeviceId: _outputDeviceId,
                            Direction: AudioStreamDirection.Loopback,
                            BufferDuration: TimeSpan.FromMilliseconds(20),
                            UseEventCallback: true,
                            FollowDefaultDevice: !useExplicitVirtual,
                            CaptureDeviceId: useExplicitVirtual ? virtualDeviceId : null)
                        {
                            UseProcessLoopbackCapture = !useExplicitVirtual,
                            ProcessLoopbackTargetProcessId = (uint)Environment.ProcessId
                        };

                        stream = _backend.Create(configuration);

                        lock (_gate)
                        {
                            _stream = stream;
                        }
                    }
                }
                else
                {
                    // Fallback when virtual endpoint is not installed (portable standalone mode on Windows 10/11)
                    string? physicalOutputId = _outputDeviceId ?? ResolveBestPhysicalOutputId() ?? _backend.Devices.GetDefaultOutput()?.Id;
                    bool useExplicitCapture = !string.IsNullOrEmpty(_captureDeviceId);
                    bool canUseProcessLoopback = !useExplicitCapture && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);
                    var configuration = new AudioStreamConfiguration(
                        DeviceId: _outputDeviceId,
                        Direction: AudioStreamDirection.Loopback,
                        BufferDuration: TimeSpan.FromMilliseconds(20),
                        UseEventCallback: true,
                        FollowDefaultDevice: !useExplicitCapture,
                        CaptureDeviceId: _captureDeviceId)
                    {
                        UseProcessLoopbackCapture = canUseProcessLoopback,
                        ProcessLoopbackTargetProcessId = (uint)Environment.ProcessId
                    };

                    stream = _backend.Create(configuration);

                    lock (_gate)
                    {
                        _stream = stream;
                        _activePhysicalOutputId = physicalOutputId;
                    }
                }
            }
            catch (Exception ex)
            {
                // If we changed Windows defaults but failed before starting the
                // audio stream, immediately put the user's original defaults back.
                if (routingSnapshot is not null)
                {
                    RestoreDefaultsAndVolume(routingSnapshot);
                }

                lock (_gate)
                {
                    _routingSnapshot = null;
                    _stream = null;

                    SetStateLocked(
                        EngineState.Faulted,
                        ex.Message);
                }

                RaiseStateChanged();
                return;
            }

            if (stream is null)
                return;

            // -------------------------------------------------------------
            // 6. Start the existing stream + existing DSP.
            // -------------------------------------------------------------
            try
            {
                stream.Processor = _processor;
                stream.Faulted += OnStreamFaulted;

                if (settings is not null)
                    _processor.UpdateSettings(settings);

                _processor.Bypass = false;

                stream.Start();

                // Ensure sessions on physical output are unmuted so audio flows freely
                if (!string.IsNullOrEmpty(_activePhysicalOutputId))
                {
                    WindowsDefaultAudioEndpointRouter.MuteNonEnhancerSessions(_activePhysicalOutputId, false);
                }

                lock (_gate)
                {
                    SetStateLocked(
                        EngineState.Running,
                        $"Enhancing {stream.CaptureDeviceName()} -> {stream.Device.Name} ({stream.Format})");
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_stream, stream))
                        _stream = null;

                    _routingSnapshot = null;
                }

                DetachStream(stream);

                if (routingSnapshot is not null)
                {
                    RestoreDefaultsAndVolume(routingSnapshot);
                }

                lock (_gate)
                {
                    SetStateLocked(
                        EngineState.Faulted,
                        ex.Message);
                }
            }

            RaiseStateChanged();
        }
    }

    public void Stop()
    {
        lock (_streamLifecycleGate)
        {
            DiagnosticLog.Write("[ENGINE] Stop");

            lock (_debounceGate)
            {
                _migrationCts?.Cancel();
                _migrationCts?.Dispose();
                _migrationCts = null;
            }

            IAudioStream? stream;
            DefaultAudioEndpointSnapshot? snapshot;

            lock (_gate)
            {
                if (_state != EngineState.NotAvailable)
                {
                    SetStateLocked(
                        EngineState.Stopped,
                        "Stopped");
                }

                stream = _stream;
                _stream = null;

                snapshot = _routingSnapshot;
                _routingSnapshot = null;
            }

            RaiseStateChanged();

            // Stop the stream outside the lock.
            if (stream is not null)
                DetachStream(stream);

            _keepAlive?.Dispose();
            _keepAlive = null;

            // Restore the user's physical playback defaults (headphones if connected, or speakers) and hardware volume.
            if (snapshot is not null)
            {
                RestoreDefaultsAndVolume(snapshot);
            }
            WindowsDefaultAudioEndpointRouter.EnsurePhysicalDefaultRestored(forceSpeakers: false);
        }
    }

    public void UpdateSettings(EnhancerSettings settings)
    {
        if (settings is null)
            return;

        lock (_gate)
            _pendingSettings = settings;

        _processor.UpdateSettings(settings);
    }

    public void SetMasterVolume(float scalar)
    {
        string? targetId;
        string? physicalId;
        lock (_gate)
        {
            targetId = _captureDeviceId ?? _activePhysicalOutputId ?? _outputDeviceId ?? _backend.Devices.GetDefaultOutput()?.Id;
            physicalId = _activePhysicalOutputId ?? _outputDeviceId ?? _backend.Devices.GetDefaultOutput()?.Id;
        }

        if (!string.IsNullOrEmpty(targetId))
        {
            WindowsDefaultAudioEndpointRouter.SetEndpointVolumeScalar(targetId, scalar);
        }

        // Apply master volume to the physical output rendering device as well as the virtual default device.
        if (!string.IsNullOrEmpty(physicalId) && !string.Equals(physicalId, targetId, StringComparison.OrdinalIgnoreCase))
        {
            WindowsDefaultAudioEndpointRouter.SetEndpointVolumeScalar(physicalId, scalar);
        }

        DiagnosticLog.Write($"[ENGINE] SetMasterVolume {scalar * 100:0}% applied to target '{targetId}' and physical '{physicalId}'");
    }

    public float? GetMasterVolume()
    {
        string? targetId;
        lock (_gate)
            targetId = _captureDeviceId ?? _activePhysicalOutputId ?? _outputDeviceId ?? _backend.Devices.GetDefaultOutput()?.Id;

        return string.IsNullOrEmpty(targetId)
            ? null
            : WindowsDefaultAudioEndpointRouter.GetEndpointVolumeScalar(targetId);
    }

    public void SetMasterMute(bool mute)
    {
        string? targetId;
        string? physicalId;
        lock (_gate)
        {
            targetId = _captureDeviceId ?? _activePhysicalOutputId ?? _outputDeviceId ?? _backend.Devices.GetDefaultOutput()?.Id;
            physicalId = _activePhysicalOutputId ?? _outputDeviceId ?? _backend.Devices.GetDefaultOutput()?.Id;
        }

        _processor.Muted = mute;

        if (!string.IsNullOrEmpty(targetId))
        {
            WindowsDefaultAudioEndpointRouter.SetEndpointMute(targetId, mute);
        }

        if (!string.IsNullOrEmpty(physicalId) && !string.Equals(physicalId, targetId, StringComparison.OrdinalIgnoreCase))
        {
            WindowsDefaultAudioEndpointRouter.SetEndpointMute(physicalId, mute);
        }
    }

    public bool? GetMasterMute()
    {
        string? targetId;
        lock (_gate)
            targetId = _captureDeviceId ?? _activePhysicalOutputId ?? _outputDeviceId ?? _backend.Devices.GetDefaultOutput()?.Id;

        return string.IsNullOrEmpty(targetId)
            ? null
            : WindowsDefaultAudioEndpointRouter.GetEndpointMute(targetId);
    }

    public void GetSpectrumAnalysis(Span<float> outBands, out float outPeakDb, out float outRmsDb, out bool isAudioActive)
    {
        _processor.SpectrumAnalyzer.GetSpectrum(outBands, out outPeakDb, out outRmsDb, out isAudioActive);
    }

    public void Dispose()
    {
        lock (_streamLifecycleGate)
        {
            try
            {
                _backend.Devices.DefaultOutputChanged -= OnPhysicalDefaultOutputChanged;
                _backend.Devices.DevicesChanged -= OnPhysicalDevicesChanged;
            }
            catch { }

            lock (_debounceGate)
            {
                _migrationCts?.Cancel();
                _migrationCts?.Dispose();
                _migrationCts = null;
            }

            IAudioStream? stream;
            DefaultAudioEndpointSnapshot? snapshot;

            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;

                stream = _stream;
                _stream = null;

                snapshot = _routingSnapshot;
                _routingSnapshot = null;
            }

            if (stream is not null)
                DetachStream(stream);

            _keepAlive?.Dispose();
            _keepAlive = null;

            // Make sure a normal application shutdown restores Windows' defaults.
            if (snapshot is not null)
            {
                RestoreDefaultsAndVolume(snapshot);
            }
            WindowsDefaultAudioEndpointRouter.EnsurePhysicalDefaultRestored();
        }
    }

    // -------------------------------------------------------------------
    // Stream fault handling & dynamic recovery
    // -------------------------------------------------------------------

    private void OnStreamFaulted(
        object? sender,
        AudioStreamFaultedEventArgs e)
    {
        DiagnosticLog.Write(
            $"[ENGINE] Fault: {e.Reason}");

        // If the active physical device was disconnected / removed while the engine is running (e.g. headphones unplugged):
        bool isDeviceRemoval =
            e.Reason.Contains("removed", StringComparison.OrdinalIgnoreCase) ||
            e.Reason.Contains("disconnected", StringComparison.OrdinalIgnoreCase) ||
            e.Reason.Contains("reconfigured", StringComparison.OrdinalIgnoreCase) ||
            e.Reason.Contains("invalidated", StringComparison.OrdinalIgnoreCase) ||
            e.Reason.Contains("0x80070490", StringComparison.OrdinalIgnoreCase);

        if (isDeviceRemoval && !_disposed && _state == EngineState.Running)
        {
            DiagnosticLog.Write("[ENGINE] Active physical device removed/invalidated while running. Migrating to speakers...");

            lock (_gate)
            {
                _outputDeviceId = null;
            }

            (_backend.Devices as IOutputDeviceProbe)?.Refresh();
            string? fallbackSpeakerId = ResolveFallbackSpeakerId();
            if (!string.IsNullOrEmpty(fallbackSpeakerId) &&
                !string.Equals(fallbackSpeakerId, _activePhysicalOutputId, StringComparison.OrdinalIgnoreCase))
            {
                Task.Run(() =>
                {
                    try
                    {
                        SwitchPhysicalOutput(fallbackSpeakerId);
                        DiagnosticLog.Write($"[ENGINE] Successfully migrated to fallback speakers: {fallbackSpeakerId}");
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.Write($"[ENGINE] Failed to migrate to fallback speakers: {ex.Message}");
                    }
                });
                return;
            }
        }

        DefaultAudioEndpointSnapshot? snapshot = null;
        bool stillRunning;

        lock (_gate)
        {
            if (_stream is null)
                return;

            stillRunning =
                _stream.State is
                    AudioStreamState.Running or
                    AudioStreamState.Opening;

            if (!stillRunning)
            {
                snapshot = _routingSnapshot;
                _routingSnapshot = null;
            }

            SetStateLocked(
                stillRunning
                    ? EngineState.Starting
                    : EngineState.Faulted,
                stillRunning
                    ? $"Reconnecting: {e.Reason}"
                    : e.Reason);
        }

        // If the stream has permanently failed, do not leave Windows pointing
        // at the virtual device.
        if (!stillRunning && snapshot is not null)
        {
            RestoreDefaultsAndVolume(snapshot);
        }

        RaiseStateChanged();
    }

    private void RestoreDefaultsAndVolume(DefaultAudioEndpointSnapshot snapshot)
    {
        try
        {
            if (_activePhysicalOutputId is not null)
            {
                float targetVol = _originalPhysicalVolume ?? 0.63f;
                bool targetMute = false;
                if (_captureDeviceId is not null)
                {
                    float? liveVol = WindowsDefaultAudioEndpointRouter.GetEndpointVolumeScalar(_captureDeviceId);
                    if (liveVol.HasValue) targetVol = liveVol.Value;
                    bool? liveMute = WindowsDefaultAudioEndpointRouter.GetEndpointMute(_captureDeviceId);
                    if (liveMute.HasValue) targetMute = liveMute.Value;
                }
                WindowsDefaultAudioEndpointRouter.SetEndpointVolumeScalar(_activePhysicalOutputId, targetVol);
                WindowsDefaultAudioEndpointRouter.SetEndpointMute(_activePhysicalOutputId, targetMute);
                WindowsDefaultAudioEndpointRouter.MuteNonEnhancerSessions(_activePhysicalOutputId, false);
                DiagnosticLog.Write($"[ENGINE] Restored physical output volume to: {targetVol * 100:0}% (mute: {targetMute})");
            }
            if (!string.IsNullOrEmpty(snapshot.MultimediaDeviceId) && !string.Equals(snapshot.MultimediaDeviceId, _activePhysicalOutputId, StringComparison.OrdinalIgnoreCase))
            {
                WindowsDefaultAudioEndpointRouter.MuteNonEnhancerSessions(snapshot.MultimediaDeviceId, false);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"[ENGINE] Failed to restore physical volume: {ex.Message}");
        }

        // On app exit or when stopped: restore to headphones if present, or physical speakers
        string? fallbackSpeakerId = ResolveFallbackSpeakerId();
        string? targetOutputId =
            ResolveBestPhysicalOutputId()
            ?? _activePhysicalOutputId
            ?? snapshot.MultimediaDeviceId
            ?? fallbackSpeakerId;

        if (!string.IsNullOrEmpty(targetOutputId))
        {
            // If the target is headphones, first set physical speakers as default in Windows.
            // This guarantees Windows Audio Service's MRU (Most Recently Used) device stack
            // has physical speakers as the immediate prior device behind headphones!
            // When headphones are later unplugged/disconnected, Windows will seamlessly
            // fall back to laptop speakers!
            if (!string.IsNullOrEmpty(fallbackSpeakerId) && !string.Equals(targetOutputId, fallbackSpeakerId, StringComparison.OrdinalIgnoreCase))
            {
                _defaultEndpointRouter.TrySetAllRoles(fallbackSpeakerId, out _);
                WindowsDefaultAudioEndpointRouter.SetEndpointMute(fallbackSpeakerId, false);
                WindowsDefaultAudioEndpointRouter.MuteNonEnhancerSessions(fallbackSpeakerId, false);
            }

            _defaultEndpointRouter.TrySetAllRoles(targetOutputId, out _);
            WindowsDefaultAudioEndpointRouter.SetEndpointMute(targetOutputId, false);
            WindowsDefaultAudioEndpointRouter.MuteNonEnhancerSessions(targetOutputId, false);
            DiagnosticLog.Write($"[ENGINE] Restored Windows default to physical device: {targetOutputId} (with speaker fallback: {fallbackSpeakerId})");
        }
        else
        {
            _defaultEndpointRouter.TryRestore(snapshot, out _);
        }

        // Hide virtual endpoint while Sonora is stopped/closed so Windows Audio Service
        // can never select or fall back to Sonora Speakers!
        if (!string.IsNullOrEmpty(_captureDeviceId))
        {
            WindowsDefaultAudioEndpointRouter.SetEndpointVisibility(_captureDeviceId, 0);
            DiagnosticLog.Write($"[ENGINE] Hidden virtual endpoint while stopped: {_captureDeviceId}");
        }
    }

    private void DetachStream(IAudioStream stream)
    {
        stream.Faulted -= OnStreamFaulted;

        try
        {
            stream.Stop();
        }
        catch
        {
            // Shutting down.
        }

        try
        {
            stream.Dispose();
        }
        catch
        {
            // Shutting down.
        }
    }

    // -------------------------------------------------------------------
    // Dynamic physical output resolution and headphone auto-routing
    // -------------------------------------------------------------------

    private string? ResolveBestPhysicalOutputId()
    {
        var physicalDevices = _backend.Devices.GetOutputDevices()
            .Where(d => !VirtualEndpointDetector.IsVirtualEndpoint(d))
            .ToList();

        if (physicalDevices.Count == 0)
            return null;

        // 1. If explicit device was selected by user in UI, check if it's available
        if (!string.IsNullOrEmpty(_outputDeviceId))
        {
            var chosen = physicalDevices.FirstOrDefault(d => string.Equals(d.Id, _outputDeviceId, StringComparison.OrdinalIgnoreCase));
            if (chosen != null)
                return chosen.Id;
        }

        // 2. Automatic mode: Prioritize Headphones / Headsets if connected!
        var headphones = physicalDevices.FirstOrDefault(d => d.IsHeadphones);
        if (headphones != null)
            return headphones.Id;

        // 3. Fallback to snapshot multimedia device if still active
        if (_routingSnapshot?.MultimediaDeviceId != null)
        {
            var original = physicalDevices.FirstOrDefault(d => string.Equals(d.Id, _routingSnapshot.MultimediaDeviceId, StringComparison.OrdinalIgnoreCase));
            if (original != null)
                return original.Id;
        }

        // 4. Default to the first active physical playback device (e.g. Speakers)
        return physicalDevices[0].Id;
    }

    private string? ResolveFallbackSpeakerId()
    {
        var physicalDevices = _backend.Devices.GetOutputDevices()
            .Where(d => !VirtualEndpointDetector.IsVirtualEndpoint(d))
            .ToList();

        if (physicalDevices.Count == 0)
            return null;

        // 1. Look for true physical speakers endpoint
        var speakers = physicalDevices.FirstOrDefault(d =>
            !d.IsHeadphones &&
            (d.Name.Contains("Speaker", StringComparison.OrdinalIgnoreCase) ||
             d.Name.Contains("Realtek", StringComparison.OrdinalIgnoreCase)));

        if (speakers != null)
            return speakers.Id;

        // 2. Any physical endpoint that is not a headphone
        var nonHeadphones = physicalDevices.FirstOrDefault(d => !d.IsHeadphones);
        if (nonHeadphones != null)
            return nonHeadphones.Id;

        return physicalDevices[0].Id;
    }

    private void ConfigurePhysicalVolume(string physicalOutputId, float? targetVolume = null)
    {
        if (_captureDeviceId == null)
        {
            // In standalone mode without virtual device, physical output is directly controlled by Windows
            return;
        }

        try
        {
            float vol = targetVolume ?? GetMasterVolume() ?? 0.5f;
            WindowsDefaultAudioEndpointRouter.SetEndpointVolumeScalar(physicalOutputId, vol);
            WindowsDefaultAudioEndpointRouter.SetEndpointMute(physicalOutputId, false);
            DiagnosticLog.Write($"[ENGINE] Configured physical output '{physicalOutputId}' to {vol * 100:0}%.");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"[ENGINE] Failed to configure volume for '{physicalOutputId}': {ex.Message}");
        }
    }

    public bool SwitchPhysicalOutput(string newPhysicalId)
    {
        lock (_streamLifecycleGate)
        {
            lock (_gate)
            {
                if (_disposed || _state != EngineState.Running)
                    return false;

                if (string.Equals(newPhysicalId, _activePhysicalOutputId, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            DiagnosticLog.Write($"[ENGINE] Seamless physical output migration: '{_activePhysicalOutputId}' -> '{newPhysicalId}'");

            IAudioStream? oldStream;
            string? virtualDeviceId;
            string? oldPhysicalId;
            lock (_gate)
            {
                oldStream = _stream;
                _stream = null;
                virtualDeviceId = _captureDeviceId;
                oldPhysicalId = _activePhysicalOutputId;
                _activePhysicalOutputId = newPhysicalId;
            }

            if (oldStream is not null)
            {
                DetachStream(oldStream);
                WindowsDefaultAudioEndpointRouter.MuteNonEnhancerSessions(oldPhysicalId, false);
            }

            try
            {
                bool isVirtualMode = !string.IsNullOrEmpty(virtualDeviceId);
                if (!isVirtualMode)
                {
                    var report = GetVirtualEndpointStatus();
                    if (report.IsInstalled && report.Device is not null)
                    {
                        virtualDeviceId = report.Device.Id;
                        isVirtualMode = true;
                    }
                }

                // CRITICAL FIX: When headphones connect, Windows automatically sets them as the default output!
                // We MUST re-assert the virtual endpoint as the Windows default for all roles so Windows applications
                // route audio into Sonora rather than directly into the headphones (which causes 0 sound or muting)!
                if (isVirtualMode && !string.IsNullOrEmpty(virtualDeviceId))
                {
                    _defaultEndpointRouter.TrySetAllRoles(virtualDeviceId, out string reassertErr);
                    DiagnosticLog.Write($"[ENGINE] Re-asserted virtual default '{virtualDeviceId}' post-switch, err: '{reassertErr}'");
                }

                // Calibrate volume appropriately for target physical device (headphones vs speakers)
                ConfigurePhysicalVolume(newPhysicalId);

                AudioStreamConfiguration configuration;
                if (isVirtualMode)
                {
                    configuration = new AudioStreamConfiguration(
                        DeviceId: newPhysicalId,
                        Direction: AudioStreamDirection.Loopback,
                        BufferDuration: TimeSpan.FromMilliseconds(20),
                        UseEventCallback: true,
                        FollowDefaultDevice: false,
                        CaptureDeviceId: virtualDeviceId)
                    {
                        UseProcessLoopbackCapture = false
                    };
                }
                else
                {
                    bool useExplicitCapture = !string.IsNullOrEmpty(_captureDeviceId);
                    bool canUseProcessLoopback = !useExplicitCapture && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);
                    configuration = new AudioStreamConfiguration(
                        DeviceId: newPhysicalId,
                        Direction: AudioStreamDirection.Loopback,
                        BufferDuration: TimeSpan.FromMilliseconds(20),
                        UseEventCallback: true,
                        FollowDefaultDevice: !useExplicitCapture,
                        CaptureDeviceId: _captureDeviceId)
                    {
                        UseProcessLoopbackCapture = canUseProcessLoopback,
                        ProcessLoopbackTargetProcessId = (uint)Environment.ProcessId
                    };
                }

                var stream = _backend.Create(configuration);
                stream.Processor = _processor;
                stream.Faulted += OnStreamFaulted;
                stream.Start();

                // Un-mute physical endpoint and ensure non-enhancer sessions are cleanly audible
                WindowsDefaultAudioEndpointRouter.SetEndpointMute(newPhysicalId, false);
                WindowsDefaultAudioEndpointRouter.MuteNonEnhancerSessions(newPhysicalId, false);

                lock (_gate)
                {
                    _stream = stream;
                    SetStateLocked(
                        EngineState.Running,
                        $"Enhancing {stream.CaptureDeviceName()} -> {stream.Device.Name} ({stream.Format})");
                }

                RaiseStateChanged();
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[ENGINE] Failed to migrate to '{newPhysicalId}': {ex.Message}");
                return false;
            }
        }
    }

    private void OnPhysicalDevicesChanged(object? sender, EventArgs e) =>
        TriggerDebouncedPhysicalMigration("DevicesChanged");

    private void OnPhysicalDefaultOutputChanged(object? sender, EventArgs e) =>
        TriggerDebouncedPhysicalMigration("DefaultOutputChanged");

    private void TriggerDebouncedPhysicalMigration(string source)
    {
        lock (_debounceGate)
        {
            _migrationCts?.Cancel();
            _migrationCts?.Dispose();
            var cts = new CancellationTokenSource();
            _migrationCts = cts;

            Task.Run(async () =>
            {
                try
                {
                    // Debounce period: allow rapid Bluetooth/jack insertion / driver events to arrive and settle
                    await Task.Delay(250, cts.Token).ConfigureAwait(false);
                    if (!cts.Token.IsCancellationRequested)
                    {
                        CheckAndMigratePhysicalOutput(source);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    DiagnosticLog.Write($"[ENGINE] Debounced migration failed: {ex.Message}");
                }
            }, cts.Token);
        }
    }

    private void CheckAndMigratePhysicalOutput(string source)
    {
        lock (_gate)
        {
            if (_disposed || _state != EngineState.Running || _stream == null)
                return;
        }

        try
        {
            (_backend.Devices as IOutputDeviceProbe)?.Refresh();

            // If user's previously selected device has been unplugged/removed, reset to auto
            if (!string.IsNullOrEmpty(_outputDeviceId))
            {
                var currentPhysicalDevices = _backend.Devices.GetOutputDevices();
                if (!currentPhysicalDevices.Any(d => string.Equals(d.Id, _outputDeviceId, StringComparison.OrdinalIgnoreCase)))
                {
                    lock (_gate)
                        _outputDeviceId = null;
                }
            }

            string? bestPhysicalId = ResolveBestPhysicalOutputId();
            if (string.IsNullOrEmpty(bestPhysicalId))
                return;

            string? currentPhysicalId;
            lock (_gate)
            {
                currentPhysicalId = _activePhysicalOutputId;
            }

            if (!string.Equals(bestPhysicalId, currentPhysicalId, StringComparison.OrdinalIgnoreCase))
            {
                DiagnosticLog.Write($"[ENGINE] {source}: Physical audio device switch detected. Migrating '{currentPhysicalId}' -> '{bestPhysicalId}'");
                SwitchPhysicalOutput(bestPhysicalId);
            }
            else
            {
                // Re-assert virtual default endpoint if Windows set the default away to the newly attached device
                var virtualStatus = GetVirtualEndpointStatus();
                if (virtualStatus.IsInstalled && virtualStatus.Device != null)
                {
                    var currentDefault = _backend.Devices.GetDefaultOutput();
                    if (!string.Equals(currentDefault.Id, virtualStatus.Device.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        _defaultEndpointRouter.TrySetAllRoles(virtualStatus.Device.Id, out string reassertErr);
                        DiagnosticLog.Write($"[ENGINE] {source}: Re-asserted virtual default '{virtualStatus.Device.Id}' because Windows default drifted to '{currentDefault.Id}', err: '{reassertErr}'");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"[ENGINE] CheckAndMigratePhysicalOutput exception: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------

    private void SetStateLocked(
        EngineState state,
        string message)
    {
        if (_state == state &&
            _statusMessage == message)
        {
            return;
        }

        _state = state;
        _statusMessage = message;
        _stateDirty = true;
    }

    private void RaiseStateChanged()
    {
        bool changed;

        lock (_gate)
        {
            changed = _stateDirty;
            _stateDirty = false;
        }

        if (changed)
            StateChanged?.Invoke(
                this,
                EventArgs.Empty);
    }
}

internal static class AudioStreamNaming
{
    /// <summary>
    /// The captured endpoint's name when the stream exposes one,
    /// otherwise a generic label.
    /// </summary>
    public static string CaptureDeviceName(
        this IAudioStream stream) =>
        stream is WasapiAudioStream wasapi
            ? wasapi.CaptureDevice.Name
            : "system audio";
}