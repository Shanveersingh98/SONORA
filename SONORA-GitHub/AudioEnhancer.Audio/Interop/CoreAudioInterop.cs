using System.Runtime.InteropServices;

namespace AudioEnhancer.Audio.Interop;

// ---------------------------------------------------------------------------
//  Raw declarations for the Windows Core Audio (MMDevice / WASAPI) API.
//
//  Nothing in this file installs anything, loads a driver, creates a virtual
//  device or writes a Windows setting. These are plain in-process COM calls
//  against mmdevapi, which is the documented, user-mode way to ask Windows
//  "which playback devices exist and what format do they run at".
//
//  Everything is `internal`: the rest of the app talks to the friendly types
//  in Devices/ and Format/, never to a COM pointer.
// ---------------------------------------------------------------------------

internal enum EDataFlow
{
    Render = 0,
    Capture = 1,
    All = 2,
}

internal enum ERole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

internal static class DeviceStateMask
{
    public const uint Active = 0x00000001;
    public const uint Disabled = 0x00000002;
    public const uint NotPresent = 0x00000004;
    public const uint Unplugged = 0x00000008;
    public const uint All = 0x0000000F;
}

internal static class ClsCtx
{
    public const uint InprocServer = 0x1;
    public const uint All = 0x17;
}

internal static class Stgm
{
    /// <summary>Read-only. We never open a property store for writing.</summary>
    public const uint Read = 0x00000000;
}

internal static class HResult
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_NOTFOUND = unchecked((int)0x80070490);
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    public const int AUDCLNT_E_SERVICE_NOT_RUNNING = unchecked((int)0x88890010);

    public static bool Failed(int hr) => hr < 0;
}

// ---------------------------------------------------------------------------
//  PROPERTYKEY / PROPVARIANT
// ---------------------------------------------------------------------------

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PropertyKey
{
    public Guid FormatId;
    public int PropertyId;

    public PropertyKey(Guid formatId, int propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }
}

internal static class PropertyKeys
{
    /// <summary>PKEY_Device_FriendlyName - "Speakers (Realtek High Definition Audio)".</summary>
    public static PropertyKey DeviceFriendlyName { get; } =
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    /// <summary>PKEY_DeviceInterface_FriendlyName - the adapter name, e.g. "Realtek High Definition Audio".</summary>
    public static PropertyKey InterfaceFriendlyName { get; } =
        new(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 6);

    /// <summary>PKEY_Device_DeviceDesc - the short form, e.g. "Speakers".</summary>
    public static PropertyKey DeviceDescription { get; } =
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);

    /// <summary>PKEY_AudioEndpoint_FormFactor - 3: Headphones, 5: Headset, 4: Earphone.</summary>
    public static PropertyKey EndpointFormFactor { get; } =
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 0);
}

/// <summary>
/// Just enough of PROPVARIANT to read a string or uint. The real union is much larger;
/// two pointer-sized fields after the 8-byte header match its size on both x86
/// (16 bytes) and x64 (24 bytes), which is all the marshaller needs.
/// Always released with PropVariantClear - the string is allocated by the callee.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort VarType;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public IntPtr Pointer;
    public IntPtr PointerHigh;

    private const ushort VT_EMPTY = 0;
    private const ushort VT_UI4 = 19;
    private const ushort VT_LPWSTR = 31;
    private const ushort VT_BSTR = 8;

    public readonly string? AsString()
    {
        if (VarType is VT_LPWSTR or VT_BSTR && Pointer != IntPtr.Zero)
            return Marshal.PtrToStringUni(Pointer);
        return null;
    }

    public readonly uint? AsUInt32()
    {
        if (VarType == VT_UI4)
            return (uint)(Pointer.ToInt64() & 0xFFFFFFFF);
        return null;
    }

    public readonly bool IsEmpty => VarType == VT_EMPTY;
}

// ---------------------------------------------------------------------------
//  WAVEFORMATEX / WAVEFORMATEXTENSIBLE
// ---------------------------------------------------------------------------

internal static class WaveFormatTag
{
    public const ushort Pcm = 0x0001;
    public const ushort IeeeFloat = 0x0003;
    public const ushort Extensible = 0xFFFE;
}

internal static class KsDataFormat
{
    public static Guid SubTypePcm { get; } = new("00000001-0000-0010-8000-00aa00389b71");
    public static Guid SubTypeIeeeFloat { get; } = new("00000003-0000-0010-8000-00aa00389b71");
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WaveFormatEx
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort ExtraSize;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WaveFormatExtensible
{
    public WaveFormatEx Format;
    /// <summary>Union of wValidBitsPerSample / wSamplesPerBlock / wReserved.</summary>
    public ushort ValidBitsPerSample;
    public uint ChannelMask;
    public Guid SubFormat;
}

// ---------------------------------------------------------------------------
//  COM interfaces
// ---------------------------------------------------------------------------

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
[ClassInterface(ClassInterfaceType.None)]
internal class MMDeviceEnumeratorComObject
{
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice? endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IMMNotificationClient client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int Item(uint index, out IMMDevice? device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid interfaceId, uint clsCtx, IntPtr activationParams,
                 [MarshalAs(UnmanagedType.IUnknown)] out object? instance);

    [PreserveSig]
    int OpenPropertyStore(uint stgmAccess, out IPropertyStore? properties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string? id);

    [PreserveSig]
    int GetState(out uint state);
}

[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int GetAt(uint index, out PropertyKey key);

    [PreserveSig]
    int GetValue(ref PropertyKey key, out PropVariant value);

    [PreserveSig]
    int SetValue(ref PropertyKey key, ref PropVariant value);

    [PreserveSig]
    int Commit();
}

/// <summary>
/// Windows calls us back on this when the audio device landscape changes.
/// The methods return void deliberately: the CLR turns a clean return into S_OK,
/// which is exactly the contract Windows expects from a notification client.
/// </summary>
[ComImport]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);

    void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    void OnDefaultDeviceChanged(EDataFlow dataFlow, ERole role,
                                [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);

    void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariantBlobActivationParams
{
    [FieldOffset(0)] public ushort VarType;
    [FieldOffset(2)] public ushort Reserved1;
    [FieldOffset(4)] public ushort Reserved2;
    [FieldOffset(6)] public ushort Reserved3;
    [FieldOffset(8)] public uint BlobSize;
    [FieldOffset(16)] public IntPtr BlobData;
}

internal static class NativeMethods
{
    public static Guid IID_IAudioClient { get; } = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static Guid IID_IAudioCaptureClient { get; } = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    public static Guid IID_IAudioRenderClient { get; } = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PropVariant value);

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr ptr);

    [DllImport("mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        ref PropVariantBlobActivationParams activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        [MarshalAs(UnmanagedType.IUnknown)] out object? activationOperation);

    public static Guid IID_IAudioSessionManager { get; } = new("BFA971F1-4D5E-40BB-935E-967039BFBEE4");
    public static Guid IID_IAudioSessionManager2 { get; } = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
}

[ComImport]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    [PreserveSig]
    int GetAudioSessionControl(ref Guid audioSessionGuid, uint streamFlags, out IntPtr sessionControl);

    [PreserveSig]
    int GetSimpleAudioVolume(ref Guid audioSessionGuid, uint streamFlags, out ISimpleAudioVolume audioVolume);

    [PreserveSig]
    int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);

    [PreserveSig]
    int RegisterSessionNotification(IntPtr newSessionNotifications);

    [PreserveSig]
    int UnregisterSessionNotification(IntPtr newSessionNotifications);

    [PreserveSig]
    int RegisterDuckNotification(string sessionId, IntPtr audioVolumeDuckNotification);

    [PreserveSig]
    int UnregisterDuckNotification(IntPtr audioVolumeDuckNotification);
}

[ComImport]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig]
    int GetCount(out int sessionCount);

    [PreserveSig]
    int GetSession(int sessionIndex, out IAudioSessionControl session);
}

[ComImport]
[Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl
{
    [PreserveSig]
    int GetState(out int state);

    [PreserveSig]
    int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

    [PreserveSig]
    int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);

    [PreserveSig]
    int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

    [PreserveSig]
    int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);

    [PreserveSig]
    int GetGroupingParam(out Guid groupingParam);

    [PreserveSig]
    int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);

    [PreserveSig]
    int RegisterAudioSessionNotification(IntPtr newNotifications);

    [PreserveSig]
    int UnregisterAudioSessionNotification(IntPtr newNotifications);
}

[ComImport]
[Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2 : IAudioSessionControl
{
    [PreserveSig]
    new int GetState(out int state);

    [PreserveSig]
    new int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

    [PreserveSig]
    new int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);

    [PreserveSig]
    new int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

    [PreserveSig]
    new int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);

    [PreserveSig]
    new int GetGroupingParam(out Guid groupingParam);

    [PreserveSig]
    new int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);

    [PreserveSig]
    new int RegisterAudioSessionNotification(IntPtr newNotifications);

    [PreserveSig]
    new int UnregisterAudioSessionNotification(IntPtr newNotifications);

    [PreserveSig]
    int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);

    [PreserveSig]
    int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);

    [PreserveSig]
    int GetProcessId(out uint retVal);

    [PreserveSig]
    int IsSystemSoundsSession();

    [PreserveSig]
    int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

[ComImport]
[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    [PreserveSig]
    int SetMasterVolume(float fLevel, ref Guid eventContext);

    [PreserveSig]
    int GetMasterVolume(out float pfLevel);

    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid eventContext);

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
}
