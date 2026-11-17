using System.Runtime.InteropServices;

namespace AudioEnhancer.Audio.Interop;

// ---------------------------------------------------------------------------
//  The WASAPI half of Core Audio: opening a stream on an endpoint, pulling
//  captured frames out of it and pushing rendered frames into it.
//
//  Part 2B stopped at GetDevicePeriod on purpose. Part 2C needs the rest, so the
//  full IAudioClient lives here and the truncated stand-in has been removed.
//
//  Still no driver, no virtual device, no injection and no Windows setting is
//  written. AUDCLNT_STREAMFLAGS_LOOPBACK is a documented, user-mode flag on an
//  ordinary shared-mode client.
// ---------------------------------------------------------------------------

internal enum AudioClientShareMode
{
    Shared = 0,
    Exclusive = 1,
}

internal static class AudioClientStreamFlags
{
    /// <summary>Capture what the render endpoint is already playing.</summary>
    public const uint Loopback = 0x00020000;

    /// <summary>Have the device signal an event when it wants servicing.</summary>
    public const uint EventCallback = 0x00040000;

    /// <summary>Keep this stream out of the user's persisted per-app volume list.</summary>
    public const uint NoPersist = 0x00080000;

    /// <summary>Enable the Windows audio engine PCM channel matrixer/sample-rate converter.</summary>
    public const uint AutoConvertPcm = 0x80000000;
}

internal static class AudioClientBufferFlags
{
    /// <summary>The packet is digital silence; its contents must be ignored.</summary>
    public const uint Silent = 0x2;

    /// <summary>The device dropped frames before this packet: reset any filter state.</summary>
    public const uint DataDiscontinuity = 0x1;
}

internal static class AudioClientHResult
{
    public const int AUDCLNT_E_NOT_INITIALIZED = unchecked((int)0x88890001);
    public const int AUDCLNT_E_ALREADY_INITIALIZED = unchecked((int)0x88890002);
    public const int AUDCLNT_E_WRONG_ENDPOINT_TYPE = unchecked((int)0x88890003);
    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    public const int AUDCLNT_E_NOT_STOPPED = unchecked((int)0x88890005);
    public const int AUDCLNT_E_BUFFER_TOO_LARGE = unchecked((int)0x88890006);
    public const int AUDCLNT_E_OUT_OF_ORDER = unchecked((int)0x88890007);
    public const int AUDCLNT_E_UNSUPPORTED_FORMAT = unchecked((int)0x88890008);
    public const int AUDCLNT_E_INVALID_SIZE = unchecked((int)0x88890009);
    public const int AUDCLNT_E_DEVICE_IN_USE = unchecked((int)0x8889000A);
    public const int AUDCLNT_E_BUFFER_OPERATION_PENDING = unchecked((int)0x8889000B);
    public const int AUDCLNT_E_SERVICE_NOT_RUNNING = unchecked((int)0x88890010);
    public const int AUDCLNT_E_EVENTHANDLE_NOT_SET = unchecked((int)0x88890014);
    public const int AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED = unchecked((int)0x88890019);
    public const int AUDCLNT_S_BUFFER_EMPTY = unchecked((int)0x08890001);

    /// <summary>
    /// True when the endpoint has gone: unplugged, disabled, format changed underneath
    /// us, or the audio service restarted. These are the cases the stream recovers from
    /// by reopening rather than giving up.
    /// </summary>
    public static bool IsRecoverable(int hr) =>
        hr == AUDCLNT_E_DEVICE_INVALIDATED ||
        hr == AUDCLNT_E_SERVICE_NOT_RUNNING ||
        hr == AUDCLNT_E_DEVICE_IN_USE;

    public static string Describe(int hr) => hr switch
    {
        AUDCLNT_E_DEVICE_INVALIDATED => "the audio device was removed or reconfigured",
        AUDCLNT_E_SERVICE_NOT_RUNNING => "the Windows Audio service is not running",
        AUDCLNT_E_DEVICE_IN_USE => "the device is held exclusively by another application",
        AUDCLNT_E_UNSUPPORTED_FORMAT => "the device rejected the stream format",
        AUDCLNT_E_WRONG_ENDPOINT_TYPE => "loopback capture needs a playback endpoint",
        AUDCLNT_E_EVENTHANDLE_NOT_SET => "the event handle was not supplied before starting",
        AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED => "the requested buffer size was not aligned",
        _ => $"Core Audio returned 0x{hr:X8}",
    };
}

[ComImport]
[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig]
    int Initialize(AudioClientShareMode shareMode, uint streamFlags, long bufferDuration,
                   long periodicity, IntPtr format, IntPtr audioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint bufferFrames);

    [PreserveSig]
    int GetStreamLatency(out long latency);

    [PreserveSig]
    int GetCurrentPadding(out uint padding);

    [PreserveSig]
    int IsFormatSupported(AudioClientShareMode shareMode, IntPtr format, out IntPtr closestMatch);

    [PreserveSig]
    int GetMixFormat(out IntPtr deviceFormat);

    [PreserveSig]
    int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(IntPtr eventHandle);

    [PreserveSig]
    int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
}

[ComImport]
[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(out IntPtr data, out uint framesToRead, out uint flags,
                  out long devicePosition, out long qpcPosition);

    [PreserveSig]
    int ReleaseBuffer(uint framesRead);

    [PreserveSig]
    int GetNextPacketSize(out uint framesInNextPacket);
}

[ComImport]
[Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    [PreserveSig]
    int GetBuffer(uint framesRequested, out IntPtr data);

    [PreserveSig]
    int ReleaseBuffer(uint framesWritten, uint flags);
}
