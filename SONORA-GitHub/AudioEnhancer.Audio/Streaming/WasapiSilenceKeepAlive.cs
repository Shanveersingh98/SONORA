using System.Runtime.InteropServices;
using System.Threading;
using AudioEnhancer.Audio.Interop;

namespace AudioEnhancer.Audio.Streaming;

/// <summary>
/// A lightweight background silence feeder for the AudioEnhancer virtual playback endpoint.
///
/// In Windows WASAPI, when all audio streams to a virtual endpoint close (such as when a
/// YouTube video ends, an ad begins, or playback is paused), the virtual driver's KS pin
/// closes and causes Core Audio to invalidate the loopback capture client (AUDCLNT_E_DEVICE_INVALIDATED).
///
/// By keeping a continuous, zero-CPU shared-mode silent render stream active on the virtual
/// endpoint, the driver pin stays open and healthy 24/7. Loopback capture never starves,
/// never faults, and video/ad transitions play seamlessly with zero interruption.
/// </summary>
internal sealed class WasapiSilenceKeepAlive : IDisposable
{
    private readonly Thread _feederThread;
    private readonly ManualResetEventSlim _stopSignal = new(false);
    private readonly string _deviceId;

    private IMMDevice? _device;
    private IAudioClient? _audioClient;
    private IAudioRenderClient? _renderClient;
    private bool _disposed;

    public WasapiSilenceKeepAlive(string deviceId)
    {
        _deviceId = deviceId;
        _feederThread = new Thread(RunFeeder)
        {
            Name = "AudioEnhancer Silence KeepAlive",
            IsBackground = true,
            Priority = ThreadPriority.Lowest
        };
    }

    public void Start()
    {
        _feederThread.Start();
    }

    private void RunFeeder()
    {
        IMMDeviceEnumerator? enumerator = null;
        IntPtr formatPtr = IntPtr.Zero;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (HResult.Failed(enumerator.GetDevice(_deviceId, out _device)) || _device == null)
            {
                DiagnosticLog.Write($"[KEEP-ALIVE] Could not find device {_deviceId}");
                return;
            }

            Guid iidClient = NativeMethods.IID_IAudioClient;
            if (HResult.Failed(_device.Activate(ref iidClient, ClsCtx.All, IntPtr.Zero, out object? clientObj)) || clientObj is not IAudioClient client)
            {
                DiagnosticLog.Write("[KEEP-ALIVE] Could not activate IAudioClient on virtual device");
                return;
            }
            _audioClient = client;

            if (HResult.Failed(_audioClient.GetMixFormat(out formatPtr)))
            {
                DiagnosticLog.Write("[KEEP-ALIVE] Could not get mix format for virtual device");
                return;
            }

            // 100ms buffer, shared mode, NoPersist so it doesn't clutter Windows volume mixer
            long bufferDurationTicks = TimeSpan.FromMilliseconds(100).Ticks;
            int hr = _audioClient.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.NoPersist,
                bufferDurationTicks,
                0,
                formatPtr,
                IntPtr.Zero);

            if (HResult.Failed(hr))
            {
                DiagnosticLog.Write($"[KEEP-ALIVE] Initialize failed: 0x{hr:X8}");
                return;
            }

            Guid iidRender = typeof(IAudioRenderClient).GUID;
            if (HResult.Failed(_audioClient.GetService(ref iidRender, out object? renderObj)) || renderObj is not IAudioRenderClient renderClient)
            {
                DiagnosticLog.Write("[KEEP-ALIVE] Could not get IAudioRenderClient");
                return;
            }
            _renderClient = renderClient;

            if (HResult.Failed(_audioClient.GetBufferSize(out uint bufferFrames)))
                return;

            // Pre-fill initial buffer with silence
            if (!HResult.Failed(_renderClient.GetBuffer(bufferFrames, out IntPtr bufferData)))
            {
                _renderClient.ReleaseBuffer(bufferFrames, AudioClientBufferFlags.Silent);
            }

            if (HResult.Failed(_audioClient.Start()))
            {
                DiagnosticLog.Write("[KEEP-ALIVE] Failed to start audio client");
                return;
            }

            DiagnosticLog.Write("[KEEP-ALIVE] Active silence keep-alive running on virtual device.");

            while (!_stopSignal.IsSet)
            {
                if (_stopSignal.Wait(40))
                    break;

                if (HResult.Failed(_audioClient.GetCurrentPadding(out uint padding)))
                    break;

                int available = (int)bufferFrames - (int)padding;
                if (available > 0)
                {
                    if (!HResult.Failed(_renderClient.GetBuffer((uint)available, out _)))
                    {
                        _renderClient.ReleaseBuffer((uint)available, AudioClientBufferFlags.Silent);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"[KEEP-ALIVE] Feeder exception: {ex.Message}");
        }
        finally
        {
            try { _audioClient?.Stop(); } catch { }
            if (formatPtr != IntPtr.Zero)
            {
                try { NativeMethods.CoTaskMemFree(formatPtr); } catch { }
            }
            ComHelpers.Release(_renderClient);
            ComHelpers.Release(_audioClient);
            ComHelpers.Release(_device);
            ComHelpers.Release(enumerator);
            DiagnosticLog.Write("[KEEP-ALIVE] Keep-alive stopped and cleaned up.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopSignal.Set();
        if (_feederThread.IsAlive)
        {
            _feederThread.Join(500);
        }
        _stopSignal.Dispose();
    }
}
