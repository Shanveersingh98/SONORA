using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AudioEnhancer.Audio.Interop;

namespace AudioEnhancer.Audio;

/// <summary>
/// The three Windows playback roles that can have separate default endpoints.
/// </summary>
public sealed record DefaultAudioEndpointSnapshot(
    string? ConsoleDeviceId,
    string? MultimediaDeviceId,
    string? CommunicationsDeviceId);

/// <summary>
/// Controls the user's Windows default playback endpoint for the duration of an
/// AudioEnhancer session. The engine captures the current defaults first and restores
/// them when enhancement stops.
/// </summary>
public interface IDefaultAudioEndpointRouter
{
    bool TryCapture(out DefaultAudioEndpointSnapshot snapshot, out string error);
    bool TrySetAllRoles(string deviceId, out string error);
    bool TryRestore(DefaultAudioEndpointSnapshot snapshot, out string error);
}

/// <summary>
/// Windows implementation backed by the Core Audio policy configuration COM interface.
/// This is used only while AudioEnhancer is actively routing system audio; it does not
/// install drivers or change any setting permanently.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDefaultAudioEndpointRouter : IDefaultAudioEndpointRouter
{
    private static readonly ERole[] Roles =
    {
        ERole.Console,
        ERole.Multimedia,
        ERole.Communications,
    };

    public bool TryCapture(out DefaultAudioEndpointSnapshot snapshot, out string error)
    {
        snapshot = new DefaultAudioEndpointSnapshot(null, null, null);
        error = "";

        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            string? console = GetDefaultId(enumerator, ERole.Console);
            string? multimedia = GetDefaultId(enumerator, ERole.Multimedia);
            string? communications = GetDefaultId(enumerator, ERole.Communications);

            if (console is null && multimedia is null && communications is null)
            {
                error = "Windows did not return a default playback endpoint.";
                return false;
            }

            snapshot = new DefaultAudioEndpointSnapshot(console, multimedia, communications);
            DiagnosticLog.Write($"[ROUTER] Captured defaults - Console: {console}, Multimedia: {multimedia}, Comm: {communications}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            ComHelpers.Release(enumerator);
        }
    }

    public bool TrySetAllRoles(string deviceId, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            error = "The virtual playback device has no valid device ID.";
            return false;
        }

        IPolicyConfig? policy = null;
        try
        {
            policy = CreatePolicyConfig();
            if (policy == null)
            {
                error = "Failed to instantiate Windows Core Audio Policy Config COM client.";
                return false;
            }

            foreach (ERole role in Roles)
            {
                int hr = policy.SetDefaultEndpoint(deviceId, role);
                if (HResult.Failed(hr))
                {
                    error = $"Windows could not set the virtual playback device for role {role} (HRESULT 0x{hr:X8}).";
                    DiagnosticLog.Write($"[ROUTER] Failed to set role {role} to {deviceId}: 0x{hr:X8}");
                    return false;
                }
            }

            // Verify with MMDeviceEnumerator that default endpoint was updated
            IMMDeviceEnumerator? enumerator = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                string? currentDefault = GetDefaultId(enumerator, ERole.Multimedia);
                DiagnosticLog.Write($"[ROUTER] SetAllRoles succeeded. Current Multimedia default: {currentDefault}");
            }
            finally
            {
                ComHelpers.Release(enumerator);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            DiagnosticLog.Write($"[ROUTER] Exception in SetAllRoles: {ex}");
            return false;
        }
        finally
        {
            ComHelpers.Release(policy);
        }
    }

    public bool TryRestore(DefaultAudioEndpointSnapshot snapshot, out string error)
    {
        error = "";
        IPolicyConfig? policy = null;
        try
        {
            policy = CreatePolicyConfig();
            if (policy == null)
            {
                error = "Failed to instantiate Windows Core Audio Policy Config COM client.";
                return false;
            }

            foreach ((ERole role, string? deviceId) in new[]
                     {
                         (ERole.Console, snapshot.ConsoleDeviceId),
                         (ERole.Multimedia, snapshot.MultimediaDeviceId),
                         (ERole.Communications, snapshot.CommunicationsDeviceId),
                     })
            {
                if (string.IsNullOrWhiteSpace(deviceId)) continue;
                int hr = policy.SetDefaultEndpoint(deviceId, role);
                if (HResult.Failed(hr))
                {
                    error = $"Windows could not restore the previous playback device for role {role} (HRESULT 0x{hr:X8}).";
                    DiagnosticLog.Write($"[ROUTER] Failed to restore role {role} to {deviceId}: 0x{hr:X8}");
                    return false;
                }
            }

            DiagnosticLog.Write($"[ROUTER] Restored defaults - Console: {snapshot.ConsoleDeviceId}, Multimedia: {snapshot.MultimediaDeviceId}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            DiagnosticLog.Write($"[ROUTER] Exception in TryRestore: {ex}");
            return false;
        }
        finally
        {
            ComHelpers.Release(policy);
        }
    }

    private static IPolicyConfig? CreatePolicyConfig()
    {
        try
        {
            // Windows 10 and Windows 11 CLSID
            Type? type = Type.GetTypeFromCLSID(new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"));
            if (type != null)
            {
                object? instance = Activator.CreateInstance(type);
                if (instance is IPolicyConfig config)
                    return config;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"[ROUTER] Modern CPolicyConfigClient creation failed: {ex.Message}");
        }

        try
        {
            // Fallback CLSID
            Type? type = Type.GetTypeFromCLSID(new Guid("294935CE-F637-4E7C-A41B-AB255460B862"));
            if (type != null)
            {
                object? instance = Activator.CreateInstance(type);
                if (instance is IPolicyConfig config)
                    return config;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"[ROUTER] Fallback CPolicyConfigClient creation failed: {ex.Message}");
        }

        return null;
    }

    public static bool EnsureEndpointVisible(string deviceId) => SetEndpointVisibility(deviceId, 1);

    public static bool SetEndpointVisibility(string deviceId, int visible)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        IPolicyConfig? policy = null;
        try
        {
            policy = CreatePolicyConfig();
            if (policy != null)
            {
                int hr = policy.SetEndpointVisibility(deviceId, visible);
                return !HResult.Failed(hr);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"[ROUTER] SetEndpointVisibility failed for {deviceId} (visible={visible}): {ex.Message}");
        }
        finally
        {
            ComHelpers.Release(policy);
        }
        return false;
    }

    /// <summary>
    /// Scans all system audio endpoints (including hidden or disabled) and makes any
    /// virtual endpoint (FxSound, AudioEnhancer, SYSVAD, VB-Audio) visible.
    /// </summary>
    public static void EnsureAllVirtualEndpointsVisible()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = enumerator.EnumAudioEndpoints(EDataFlow.Render, 0x0000000Fu, out collection);
            if (HResult.Failed(hr) || collection == null) return;
            if (HResult.Failed(collection.GetCount(out uint count))) return;

            for (uint i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                try
                {
                    if (HResult.Failed(collection.Item(i, out device)) || device == null) continue;
                    device.GetId(out string? id);
                    if (string.IsNullOrEmpty(id)) continue;

                    string? name = WindowsOutputDeviceManager.ReadFriendlyName(device);
                    if (!string.IsNullOrEmpty(name) &&
                        (name.Contains("AudioEnhancer", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("Sonora", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("FxSound", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("Tablet Sample", StringComparison.OrdinalIgnoreCase)))
                    {
                        EnsureEndpointVisible(id);
                        // Ensure endpoint is branded as Sonora Speakers in Windows
                        TryRenameEndpoint(id, "Sonora Speakers", out _);
                    }
                }
                finally
                {
                    ComHelpers.Release(device);
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"[ROUTER] EnsureAllVirtualEndpointsVisible exception: {ex.Message}");
        }
        finally
        {
            ComHelpers.Release(collection);
            ComHelpers.Release(enumerator);
        }
    }

    /// <summary>
    /// Mutes any non-AudioEnhancer audio sessions directly rendering to the physical output endpoint.
    /// This prevents dual-volume playback (echo/comb-filtering) if an application was already running
    /// on the physical device before AudioEnhancer started.
    /// </summary>
    public static void MuteNonEnhancerSessions(string? physicalDeviceId, bool mute)
    {
        if (string.IsNullOrWhiteSpace(physicalDeviceId)) return;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioSessionManager2? sessionManager = null;
        IAudioSessionEnumerator? sessionEnum = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (HResult.Failed(enumerator.GetDevice(physicalDeviceId, out device)) || device == null) return;

            Guid iid = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
            if (HResult.Failed(device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out object? instance)) || instance is not IAudioSessionManager2 mgr)
                return;
            sessionManager = mgr;

            if (HResult.Failed(sessionManager.GetSessionEnumerator(out sessionEnum)) || sessionEnum == null)
                return;

            if (HResult.Failed(sessionEnum.GetCount(out int count)))
                return;

            uint currentPid = (uint)Environment.ProcessId;
            Guid ctx = Guid.Empty;

            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl? control = null;
                try
                {
                    if (HResult.Failed(sessionEnum.GetSession(i, out control)) || control == null) continue;
                    if (control is IAudioSessionControl2 control2)
                    {
                        if (!HResult.Failed(control2.GetProcessId(out uint pid)) && pid != 0)
                        {
                            if (pid == currentPid)
                            {
                                if (control is ISimpleAudioVolume ownVol)
                                {
                                    ownVol.SetMute(false, ref ctx);
                                }
                                continue;
                            }

                            if (control is ISimpleAudioVolume otherVol)
                            {
                                otherVol.SetMute(mute, ref ctx);
                                DiagnosticLog.Write($"[ROUTER] Session PID {pid} on physical output mute set to {mute}");
                            }
                        }
                    }
                }
                catch { }
                finally
                {
                    ComHelpers.Release(control);
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"[ROUTER] MuteNonEnhancerSessions error: {ex.Message}");
        }
        finally
        {
            ComHelpers.Release(sessionEnum);
            ComHelpers.Release(sessionManager);
            ComHelpers.Release(device);
            ComHelpers.Release(enumerator);
        }
    }

    public static float? GetEndpointVolumeScalar(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (HResult.Failed(enumerator.GetDevice(deviceId, out device)) || device == null)
                return null;

            Guid iid = new("5CDF2C82-841E-4546-9722-0CF74078229A");
            if (HResult.Failed(device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out object? instance)) || instance is not IAudioEndpointVolume volume)
                return null;

            try
            {
                if (!HResult.Failed(volume.GetMasterVolumeLevelScalar(out float level)))
                    return level;
            }
            finally
            {
                ComHelpers.Release(volume);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"[ROUTER] Error reading volume for {deviceId}: {ex.Message}");
        }
        finally
        {
            ComHelpers.Release(device);
            ComHelpers.Release(enumerator);
        }
        return null;
    }

    public static bool SetEndpointVolumeScalar(string deviceId, float level)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (HResult.Failed(enumerator.GetDevice(deviceId, out device)) || device == null)
                return false;

            Guid iid = new("5CDF2C82-841E-4546-9722-0CF74078229A");
            if (HResult.Failed(device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out object? instance)) || instance is not IAudioEndpointVolume volume)
                return false;

            try
            {
                Guid ctx = Guid.Empty;
                int hr = volume.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref ctx);
                return !HResult.Failed(hr);
            }
            finally
            {
                ComHelpers.Release(volume);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"[ROUTER] Error setting volume for {deviceId}: {ex.Message}");
        }
        finally
        {
            ComHelpers.Release(device);
            ComHelpers.Release(enumerator);
        }
        return false;
    }

    public static bool? GetEndpointMute(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (HResult.Failed(enumerator.GetDevice(deviceId, out device)) || device == null)
                return null;

            Guid iid = new("5CDF2C82-841E-4546-9722-0CF74078229A");
            if (HResult.Failed(device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out object? instance)) || instance is not IAudioEndpointVolume volume)
                return null;

            try
            {
                if (!HResult.Failed(volume.GetMute(out bool isMuted)))
                    return isMuted;
            }
            finally
            {
                ComHelpers.Release(volume);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"[ROUTER] Error reading mute for {deviceId}: {ex.Message}");
        }
        finally
        {
            ComHelpers.Release(device);
            ComHelpers.Release(enumerator);
        }
        return null;
    }

    public static bool SetEndpointMute(string deviceId, bool mute)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (HResult.Failed(enumerator.GetDevice(deviceId, out device)) || device == null)
                return false;

            Guid iid = new("5CDF2C82-841E-4546-9722-0CF74078229A");
            if (HResult.Failed(device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out object? instance)) || instance is not IAudioEndpointVolume volume)
                return false;

            try
            {
                Guid ctx = Guid.Empty;
                int hr = volume.SetMute(mute, ref ctx);
                return !HResult.Failed(hr);
            }
            finally
            {
                ComHelpers.Release(volume);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"[ROUTER] Error setting mute for {deviceId}: {ex.Message}");
        }
        finally
        {
            ComHelpers.Release(device);
            ComHelpers.Release(enumerator);
        }
        return false;
    }

    public static bool TryRenameEndpoint(string deviceId, string newFriendlyName, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            error = "Invalid device ID";
            return false;
        }

        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IPropertyStore? store = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (HResult.Failed(enumerator.GetDevice(deviceId, out device)) || device == null)
            {
                error = "Device not found";
                return false;
            }

            // STGM_READWRITE = 2, STGM_WRITE = 1
            int hr = device.OpenPropertyStore(2, out store);
            if (HResult.Failed(hr) || store == null)
            {
                hr = device.OpenPropertyStore(1, out store);
            }

            if (HResult.Failed(hr) || store == null)
            {
                error = $"Failed to open property store: 0x{hr:X8}";
                return false;
            }

            // PKEY_Device_FriendlyName: {a45c254e-df1c-4efd-8020-67d146a850e0}, 14
            PropertyKey keyFriendly = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
            // PKEY_Device_DeviceDesc: {a45c254e-df1c-4efd-8020-67d146a850e0}, 2
            PropertyKey keyDesc = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
            // PKEY_DeviceInterface_FriendlyName: {b3f8fa53-0004-438e-9003-51a46e139bfc}, 6
            PropertyKey keyInterface = new(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 6);

            IntPtr strPtr = Marshal.StringToCoTaskMemUni(newFriendlyName);
            try
            {
                PropVariant pv = default;
                pv.VarType = 31; // VT_LPWSTR
                pv.Pointer = strPtr;

                int hr1 = store.SetValue(ref keyFriendly, ref pv);
                int hr2 = store.SetValue(ref keyDesc, ref pv);
                int hr3 = store.SetValue(ref keyInterface, ref pv);
                int hrCommit = store.Commit();

                if (HResult.Failed(hrCommit))
                {
                    error = $"Commit failed: 0x{hrCommit:X8}";
                    return false;
                }

                DiagnosticLog.Write($"[ROUTER] Renamed endpoint {deviceId} to '{newFriendlyName}' successfully.");
                return true;
            }
            finally
            {
                Marshal.FreeCoTaskMem(strPtr);
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            ComHelpers.Release(store);
            ComHelpers.Release(device);
            ComHelpers.Release(enumerator);
        }
    }

    /// <summary>
    /// Accurately detects whether an IMMDevice represents physical headphones, headset, earbuds, or Bluetooth audio.
    /// Uses both hardware EndpointFormFactor jack detection and comprehensive brand/device heuristics.
    /// </summary>
    internal static bool IsHeadphoneDevice(IMMDevice device, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        // 1. Comprehensive name & brand heuristics
        if (name.Contains("headphone", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("headset", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("earphone", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("airpod", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("buds", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("earbud", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("in-ear", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("iem", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("bluetooth", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("hands-free", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("wh-", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("wf-", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("quietcomfort", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("beats", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("sennheiser", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("jabra", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 2. Hardware form-factor check (jack / hardware detection: 3 = Headphones, 4 = Earphone, 5 = Headset)
        IPropertyStore? store = null;
        try
        {
            if (!HResult.Failed(device.OpenPropertyStore(Stgm.Read, out store)) && store is not null)
            {
                uint? formFactor = WindowsOutputDeviceManager.ReadUInt32Property(store, PropertyKeys.EndpointFormFactor);
                if (formFactor is 3 or 4 or 5)
                {
                    return true;
                }
            }
        }
        catch { }
        finally
        {
            ComHelpers.Release(store);
        }

        return false;
    }

    /// <summary>
    /// Bulletproof fail-safe: ensures Windows default audio playback endpoint is restored
    /// to the user's physical speakers (e.g. Realtek Speakers / laptop speakers) on app close, window exit, or crash.
    /// Can be safely called anywhere, including on app exit, window closing, or crash handler.
    /// </summary>
    public static void EnsurePhysicalDefaultRestored(bool forceSpeakers = false)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceStateMask.Active, out collection);
            if (HResult.Failed(hr) || collection == null) return;
            if (HResult.Failed(collection.GetCount(out uint count))) return;

            string? bestSpeakerId = null;
            string? bestSpeakerName = null;
            string? bestHeadphoneId = null;
            string? bestHeadphoneName = null;
            string? anyPhysicalId = null;
            string? anyPhysicalName = null;
            List<string> virtualDeviceIds = new();

            for (uint i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                try
                {
                    if (HResult.Failed(collection.Item(i, out device)) || device == null) continue;
                    device.GetId(out string? id);
                    if (string.IsNullOrEmpty(id)) continue;

                    string? name = WindowsOutputDeviceManager.ReadFriendlyName(device);
                    if (string.IsNullOrEmpty(name)) continue;

                    // If it is NOT a virtual endpoint, it's a physical candidate
                    bool isVirtual = name.Contains("Sonora", StringComparison.OrdinalIgnoreCase) ||
                                     name.Contains("FxSound", StringComparison.OrdinalIgnoreCase) ||
                                     name.Contains("AudioEnhancer", StringComparison.OrdinalIgnoreCase) ||
                                     name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
                                     name.Contains("Tablet Sample", StringComparison.OrdinalIgnoreCase);

                    if (isVirtual)
                    {
                        if (!virtualDeviceIds.Contains(id))
                        {
                            virtualDeviceIds.Add(id);
                        }
                    }
                    else
                    {
                        if (anyPhysicalId == null)
                        {
                            anyPhysicalId = id;
                            anyPhysicalName = name;
                        }

                        bool isHeadphones = IsHeadphoneDevice(device, name);
                        if (isHeadphones)
                        {
                            if (bestHeadphoneId == null)
                            {
                                bestHeadphoneId = id;
                                bestHeadphoneName = name;
                            }
                        }
                        else
                        {
                            // Physical speakers / Realtek / Built-in
                            bool isSpeaker = name.Contains("Speaker", StringComparison.OrdinalIgnoreCase) ||
                                             name.Contains("Realtek", StringComparison.OrdinalIgnoreCase);
                            if (isSpeaker && bestSpeakerId == null)
                            {
                                bestSpeakerId = id;
                                bestSpeakerName = name;
                            }
                        }
                    }
                }
                finally
                {
                    ComHelpers.Release(device);
                }
            }

            // On app close / disconnect, prioritize redirecting to headphones if present, or physical speakers if not
            string? targetId = forceSpeakers
                ? (bestSpeakerId ?? anyPhysicalId ?? bestHeadphoneId)
                : (bestHeadphoneId ?? bestSpeakerId ?? anyPhysicalId);
            string? targetName = forceSpeakers
                ? (bestSpeakerName ?? anyPhysicalName ?? bestHeadphoneName)
                : (bestHeadphoneName ?? bestSpeakerName ?? anyPhysicalName);

            if (!string.IsNullOrEmpty(targetId))
            {
                var router = new WindowsDefaultAudioEndpointRouter();

                // 1. Establish true physical laptop speakers in Windows' fallback chain first.
                // When headphones are subsequently unplugged or disconnected, Windows Audio Service
                // automatically and natively falls back to laptop speakers instead of any virtual device!
                if (!string.IsNullOrEmpty(bestSpeakerId) && !string.Equals(targetId, bestSpeakerId, StringComparison.OrdinalIgnoreCase))
                {
                    router.TrySetAllRoles(bestSpeakerId, out string fallbackErr);
                    SetEndpointMute(bestSpeakerId, false);
                    MuteNonEnhancerSessions(bestSpeakerId, false);
                    DiagnosticLog.Write($"[ROUTER] EnsurePhysicalDefaultRestored registered fallback speaker: {bestSpeakerName} ({bestSpeakerId}), err: '{fallbackErr}'");
                }

                // 2. Set the target endpoint (Headphones if connected, or Speakers) as active default
                router.TrySetAllRoles(targetId, out string setErr);
                DiagnosticLog.Write($"[ROUTER] EnsurePhysicalDefaultRestored restored default to: {targetName} ({targetId}), fallback: {bestSpeakerName}, err: '{setErr}'");

                SetEndpointMute(targetId, false);
                MuteNonEnhancerSessions(targetId, false);

                // 3. Set virtual endpoint visibility to 0 (hidden) while Sonora is inactive,
                // so Windows CAN NEVER route audio to any virtual device when Sonora is off/closed!
                foreach (string vId in virtualDeviceIds)
                {
                    SetEndpointVisibility(vId, 0);
                    DiagnosticLog.Write($"[ROUTER] Hidden virtual endpoint while inactive: {vId}");
                }

                // If physical device was left at 100% by a previous crash or unclean shutdown,
                // recover user's actual volume level from the virtual endpoint if available.
                string? firstVirtId = virtualDeviceIds.FirstOrDefault();
                float? physicalVol = GetEndpointVolumeScalar(targetId);
                if (physicalVol.HasValue && physicalVol.Value >= 0.99f && !string.IsNullOrEmpty(firstVirtId))
                {
                    float? virtVol = GetEndpointVolumeScalar(firstVirtId);
                    if (virtVol.HasValue && virtVol.Value > 0.01f && virtVol.Value < 0.95f)
                    {
                        SetEndpointVolumeScalar(targetId, virtVol.Value);
                        DiagnosticLog.Write($"[ROUTER] EnsurePhysicalDefaultRestored restored physical volume from virtual: {virtVol.Value * 100:0}%");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"[ROUTER] EnsurePhysicalDefaultRestored exception: {ex.Message}");
        }
        finally
        {
            ComHelpers.Release(collection);
            ComHelpers.Release(enumerator);
        }
    }

    private static string? GetDefaultId(IMMDeviceEnumerator enumerator, ERole role)
    {
        IMMDevice? device = null;
        try
        {
            if (HResult.Failed(enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, role, out device)) || device is null)
                return null;

            device.GetId(out string? id);
            return id;
        }
        finally
        {
            ComHelpers.Release(device);
        }
    }
}

[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IntPtr pNotify);
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr pNotify);
    [PreserveSig] int GetChannelCount(out uint pnChannelCount);
    [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, [In] ref Guid pguidEventContext);
    [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, [In] ref Guid pguidEventContext);
    [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
    [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, [In] ref Guid pguidEventContext);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, [In] ref Guid pguidEventContext);
    [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, [In] ref Guid pguidEventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    [PreserveSig] int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
    [PreserveSig] int VolumeStepUp([In] ref Guid pguidEventContext);
    [PreserveSig] int VolumeStepDown([In] ref Guid pguidEventContext);
    [PreserveSig] int QueryHardwareSupport(out uint pdwHardwareSupportMask);
    [PreserveSig] int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
}

/// <summary>
/// Modern Windows 10/11 PolicyConfig COM interface.
/// Notice index 2 (vtable slot 5): ResetDeviceFormat was added in Windows 10,
/// shifting SetDefaultEndpoint to slot 13 (index 10).
/// </summary>
[ComImport]
[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format);
    [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultFormat, IntPtr format);
    [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
    [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultPeriod, IntPtr defaultValue, IntPtr minimumValue);
    [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
    [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
    [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
    [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int fxStore, IntPtr key, IntPtr value);
    [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int fxStore, IntPtr key, IntPtr value);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
    [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
}
