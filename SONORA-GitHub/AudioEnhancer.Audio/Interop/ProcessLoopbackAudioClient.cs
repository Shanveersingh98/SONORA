using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AudioEnhancer.Audio.Interop;

/// <summary>
/// Activates Windows' process-loopback audio client. This is a Windows 10 20348+ /
/// Windows 11 API path that captures rendered application audio without creating a
/// virtual playback device. The caller can exclude a process tree, which is how the
/// AudioEnhancer process avoids capturing its own rendered output.
/// </summary>
[SupportedOSPlatform("windows10.0.20348")]
internal static class ProcessLoopbackAudioClient
{
    public const string DeviceInterfacePath = "VAD\\Process_Loopback";
    public const string SyntheticDeviceId = "process-loopback";
    public const string SyntheticDeviceName = "System Audio (Windows Process Loopback)";

    private const ushort VT_BLOB = 65;

    public static IAudioClient ActivateExcludingProcessTree(uint processId)
    {
        if (processId == 0)
            throw new ArgumentOutOfRangeException(nameof(processId));

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
        {
            throw new AudioBackendException(
                "System-wide process-loopback capture requires Windows 10 build 20348 or newer.");
        }

        var activation = new AudioClientActivationParams
        {
            ActivationType = AudioClientActivationType.ProcessLoopback,
            ProcessLoopbackParams = new ProcessLoopbackParams
            {
                TargetProcessId = processId,
                ProcessLoopbackMode = ProcessLoopbackMode.ExcludeTargetProcessTree,
            },
        };

        int activationSize = Marshal.SizeOf<AudioClientActivationParams>();
        IntPtr activationPointer = Marshal.AllocCoTaskMem(activationSize);
        try
        {
            Marshal.StructureToPtr(activation, activationPointer, fDeleteOld: false);

            var activationParams = new PropVariantBlobActivationParams
            {
                VarType = VT_BLOB,
                BlobSize = (uint)activationSize,
                BlobData = activationPointer,
            };

            var completion = new ActivationCompletionHandler();
            Guid audioClientIid = NativeMethods.IID_IAudioClient;
            int hr = NativeMethods.ActivateAudioInterfaceAsync(
                DeviceInterfacePath,
                ref audioClientIid,
                ref activationParams,
                completion,
                out object? operation);

            if (HResult.Failed(hr))
                throw new AudioBackendException(
                    $"could not start Windows process-loopback activation (HRESULT 0x{hr:X8}).");

            // The callback is delivered on an MTA thread. We are already on the stream's
            // MTA engine thread, so waiting here does not block a UI/STA message pump.
            try
            {
                return completion.WaitForResult();
            }
            finally
            {
                ComHelpers.Release(operation);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(activationPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public AudioClientActivationType ActivationType;
        public ProcessLoopbackParams ProcessLoopbackParams;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessLoopbackParams
    {
        public uint TargetProcessId;
        public ProcessLoopbackMode ProcessLoopbackMode;
    }

    private enum AudioClientActivationType : uint
    {
        Default = 0,
        ProcessLoopback = 1,
    }

    private enum ProcessLoopbackMode : uint
    {
        IncludeTargetProcessTree = 0,
        ExcludeTargetProcessTree = 1,
    }

    // ActivateAudioInterfaceAsync invokes the handler from a Windows-owned thread. The handler
    // MUST therefore be agile: it has to answer QueryInterface(IAgileObject), otherwise mmdevapi
    // cannot hand it to that thread and rejects the call before any activation happens. A plain
    // .NET COM-callable wrapper does not report IAgileObject on its own, so it is declared here.
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivationCompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly ManualResetEventSlim _completed = new(false);
        private IAudioClient? _audioClient;
        private Exception? _error;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                int hr = operation.GetActivateResult(
                    out int activationResult,
                    out object? activatedInterface);

                if (HResult.Failed(hr))
                {
                    _error = new AudioBackendException(
                        $"Windows could not return the process-loopback activation result (HRESULT 0x{hr:X8}).");
                }
                else if (HResult.Failed(activationResult))
                {
                    _error = new AudioBackendException(
                        $"Windows could not activate process-loopback capture (HRESULT 0x{activationResult:X8}).");
                }
                else if (activatedInterface is not IAudioClient audioClient)
                {
                    _error = new AudioBackendException(
                        "Windows process-loopback activation did not return an audio client.");
                }
                else
                {
                    _audioClient = audioClient;
                }
            }
            catch (Exception ex)
            {
                _error = ex;
            }
            finally
            {
                _completed.Set();
            }

            return HResult.S_OK;
        }

        public IAudioClient WaitForResult()
        {
            _completed.Wait();
            _completed.Dispose();

            if (_error is not null) throw _error;
            return _audioClient
                ?? throw new AudioBackendException("Windows process-loopback activation returned no audio client.");
        }
    }
}

/// <summary>Marker interface (no methods): "this object may be called from any apartment".</summary>
[ComImport]
[Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject
{
}

[ComImport]
[Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig]
    int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
}

[ComImport]
[Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig]
    int GetActivateResult(
        out int activationResult,
        [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
}
