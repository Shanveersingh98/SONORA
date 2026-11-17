using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AudioEnhancer.Audio.Interop;

namespace AudioEnhancer.Audio;

/// <summary>
/// Real playback-device detection, using the Windows Core Audio device enumerator.
///
/// This replaces <see cref="NullOutputDeviceManager"/> from Part 1. It only ever
/// reads: it enumerates endpoints, reads their friendly names and asks which one is
/// the default. It installs nothing, changes no Windows setting and opens no stream.
///
/// Every public member is defensive. If mmdevapi is unavailable - a stripped Windows
/// install, the audio service stopped, a remote session - the manager reports a single
/// "unavailable" device instead of throwing, so the UI still starts.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsOutputDeviceManager : IOutputDeviceManager, IOutputDeviceProbe, IDisposable
{
    private static readonly AudioDeviceInfo Unavailable =
        new("unavailable", "No playback device detected", true);

    private readonly object _gate = new();
    private readonly NotificationClient? _notifications;

    private IMMDeviceEnumerator? _enumerator;
    private IReadOnlyList<AudioDeviceInfo> _cachedDevices = Array.Empty<AudioDeviceInfo>();
    private AudioDeviceInfo? _cachedDefault;
    private bool _cacheValid;
    private bool _disposed;

    public WindowsOutputDeviceManager()
    {
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        }
        catch (Exception ex)
        {
            _enumerator = null;
            LastError = ex.Message;
        }

        if (_enumerator is not null)
        {
            try
            {
                _notifications = new NotificationClient(this);
                int hr = _enumerator.RegisterEndpointNotificationCallback(_notifications);
                if (HResult.Failed(hr))
                    _notifications = null;   // no live updates, but listing still works
            }
            catch
            {
                _notifications = null;
            }
        }
    }

    /// <summary>True when Windows Core Audio answered. False means the fallback device is being reported.</summary>
    public bool IsAvailable => _enumerator is not null;

    /// <summary>Why detection failed, when it did. Empty on success.</summary>
    public string LastError { get; private set; } = "";

    public event EventHandler? DefaultOutputChanged;
    public event EventHandler? DevicesChanged;

    // -----------------------------------------------------------------------
    //  IOutputDeviceManager
    // -----------------------------------------------------------------------

    public AudioDeviceInfo GetDefaultOutput()
    {
        lock (_gate)
        {
            EnsureCache();
            return _cachedDefault ?? Unavailable;
        }
    }

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        lock (_gate)
        {
            EnsureCache();
            return _cachedDevices.Count > 0 ? _cachedDevices : new[] { Unavailable };
        }
    }

    // -----------------------------------------------------------------------
    //  IOutputDeviceProbe
    // -----------------------------------------------------------------------

    public void Refresh()
    {
        lock (_gate) _cacheValid = false;
    }

    /// <summary>
    /// Activates IAudioClient on the endpoint purely to read its mix format and
    /// device period. Initialize() is never called, so no audio stream is opened and
    /// nothing is claimed from the device.
    /// </summary>
    public AudioDeviceCapabilities? GetCapabilities(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return null;

        IMMDevice? device = null;
        try
        {
            lock (_gate)
            {
                IMMDeviceEnumerator? enumerator = _enumerator;
                if (enumerator is null) return null;
                if (HResult.Failed(enumerator.GetDevice(deviceId, out device)) || device is null)
                    return null;
            }

            bool isDefault = string.Equals(GetDefaultOutput().Id, deviceId, StringComparison.OrdinalIgnoreCase);
            AudioDeviceInfo info = Describe(device!, isDefault) ?? Unavailable;
            return ReadCapabilities(device!, info);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
        finally
        {
            ComHelpers.Release(device);
        }
    }

    /// <summary>Convenience wrapper: capabilities of whichever device Windows is currently playing through.</summary>
    public AudioDeviceCapabilities? GetDefaultCapabilities()
    {
        AudioDeviceInfo defaultDevice = GetDefaultOutput();
        return defaultDevice.Id is "unavailable" or "none"
            ? null
            : GetCapabilities(defaultDevice.Id);
    }

    // -----------------------------------------------------------------------
    //  Enumeration
    // -----------------------------------------------------------------------

    private void EnsureCache()
    {
        if (_cacheValid) return;

        _cachedDevices = Array.Empty<AudioDeviceInfo>();
        _cachedDefault = null;
        _cacheValid = true;

        IMMDeviceEnumerator? enumerator = _enumerator;
        if (enumerator is null) return;

        string? defaultId = null;
        IMMDevice? defaultDevice = null;
        try
        {
            if (!HResult.Failed(enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out defaultDevice))
                && defaultDevice is not null)
            {
                defaultDevice.GetId(out defaultId);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            ComHelpers.Release(defaultDevice);
        }

        IMMDeviceCollection? collection = null;
        try
        {
            int hr = enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceStateMask.Active, out collection);
            if (HResult.Failed(hr) || collection is null) return;

            if (HResult.Failed(collection.GetCount(out uint count))) return;

            var found = new List<AudioDeviceInfo>((int)count);
            for (uint i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                try
                {
                    if (HResult.Failed(collection.Item(i, out device)) || device is null) continue;

                    device.GetId(out string? id);
                    AudioDeviceInfo? info = Describe(device, id is not null && id == defaultId);
                    if (info is not null) found.Add(info);
                }
                catch
                {
                    // One awkward endpoint must not hide the rest of the list.
                }
                finally
                {
                    ComHelpers.Release(device);
                }
            }

            _cachedDevices = found;
            _cachedDefault = found.FirstOrDefault(d => d.IsDefault) ?? found.FirstOrDefault();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            ComHelpers.Release(collection);
        }
    }

    internal static AudioDeviceInfo? Describe(IMMDevice device, bool isDefault)
    {
        if (HResult.Failed(device.GetId(out string? id)) || string.IsNullOrEmpty(id))
            return null;

        string name = "Unnamed playback device";
        bool isHeadphoneHardware = false;

        IPropertyStore? store = null;
        try
        {
            if (!HResult.Failed(device.OpenPropertyStore(Stgm.Read, out store)) && store is not null)
            {
                name = ReadStringProperty(store, PropertyKeys.DeviceFriendlyName)
                    ?? ReadStringProperty(store, PropertyKeys.DeviceDescription)
                    ?? ReadStringProperty(store, PropertyKeys.InterfaceFriendlyName)
                    ?? name;

                uint? formFactor = ReadUInt32Property(store, PropertyKeys.EndpointFormFactor);
                // Windows EndpointFormFactor enum: 3 = Headphones, 4 = Earphone, 5 = Headset
                if (formFactor is 3 or 4 or 5)
                {
                    isHeadphoneHardware = true;
                }
            }
        }
        catch
        {
        }
        finally
        {
            ComHelpers.Release(store);
        }

        return new AudioDeviceInfo(id, name, isDefault, isHeadphoneHardware);
    }

    internal static string? ReadFriendlyName(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            if (HResult.Failed(device.OpenPropertyStore(Stgm.Read, out store)) || store is null)
                return null;

            return ReadStringProperty(store, PropertyKeys.DeviceFriendlyName)
                ?? ReadStringProperty(store, PropertyKeys.DeviceDescription)
                ?? ReadStringProperty(store, PropertyKeys.InterfaceFriendlyName);
        }
        catch
        {
            return null;
        }
        finally
        {
            ComHelpers.Release(store);
        }
    }

    internal static uint? ReadUInt32Property(IPropertyStore store, PropertyKey key)
    {
        PropVariant value = default;
        try
        {
            if (HResult.Failed(store.GetValue(ref key, out value))) return null;
            return value.AsUInt32();
        }
        catch
        {
            return null;
        }
        finally
        {
            try { NativeMethods.PropVariantClear(ref value); } catch { }
        }
    }

    private static string? ReadStringProperty(IPropertyStore store, PropertyKey key)
    {
        PropVariant value = default;
        try
        {
            if (HResult.Failed(store.GetValue(ref key, out value))) return null;
            string? text = value.AsString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { NativeMethods.PropVariantClear(ref value); } catch { /* nothing left to do */ }
        }
    }

    private static AudioDeviceCapabilities? ReadCapabilities(IMMDevice device, AudioDeviceInfo info)
    {
        object? activated = null;
        IntPtr formatPointer = IntPtr.Zero;
        try
        {
            Guid iid = NativeMethods.IID_IAudioClient;
            if (HResult.Failed(device.Activate(ref iid, ClsCtx.InprocServer, IntPtr.Zero, out activated))
                || activated is null)
                return null;

            if (activated is not IAudioClient client) return null;

            if (HResult.Failed(client.GetMixFormat(out formatPointer)) || formatPointer == IntPtr.Zero)
                return null;

            AudioStreamFormat? format = AudioStreamFormat.FromWaveFormat(formatPointer);
            if (format is null) return null;

            TimeSpan defaultPeriod = TimeSpan.Zero;
            TimeSpan minimumPeriod = TimeSpan.Zero;
            if (!HResult.Failed(client.GetDevicePeriod(out long defaultTicks, out long minimumTicks)))
            {
                // REFERENCE_TIME is in 100-nanosecond units, the same unit as TimeSpan ticks.
                defaultPeriod = TimeSpan.FromTicks(defaultTicks);
                minimumPeriod = TimeSpan.FromTicks(minimumTicks);
            }

            return new AudioDeviceCapabilities(info, format, defaultPeriod, minimumPeriod);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (formatPointer != IntPtr.Zero) NativeMethods.CoTaskMemFree(formatPointer);
            ComHelpers.Release(activated);
        }
    }

    // -----------------------------------------------------------------------
    //  Change notifications
    // -----------------------------------------------------------------------

    private void OnDefaultChanged()
    {
        Refresh();
        DefaultOutputChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnListChanged()
    {
        Refresh();
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Windows calls this on one of its own threads. It does nothing but invalidate
    /// the cache and raise an event - no COM calls, no UI work - so there is nothing
    /// here that can deadlock the audio service.
    /// </summary>
    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly WindowsOutputDeviceManager _owner;

        public NotificationClient(WindowsOutputDeviceManager owner) => _owner = owner;

        public void OnDeviceStateChanged(string deviceId, uint newState) => Safe(_owner.OnListChanged);

        public void OnDeviceAdded(string deviceId) => Safe(_owner.OnListChanged);

        public void OnDeviceRemoved(string deviceId) => Safe(_owner.OnListChanged);

        public void OnDefaultDeviceChanged(EDataFlow dataFlow, ERole role, string? defaultDeviceId)
        {
            if (dataFlow != EDataFlow.Render) return;
            if (role != ERole.Multimedia && role != ERole.Console) return;
            Safe(_owner.OnDefaultChanged);
        }

        public void OnPropertyValueChanged(string deviceId, PropertyKey key)
        {
            // Friendly names can be renamed under us; drop the cache so the next read is fresh.
            Safe(_owner.Refresh);
        }

        /// <summary>
        /// An exception escaping into COM would be returned to Windows as a failed
        /// HRESULT and can get the client unregistered. Swallow instead.
        /// </summary>
        private static void Safe(Action action)
        {
            try { action(); } catch { /* never let a notification take the app down */ }
        }
    }

    // -----------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_gate)
        {
            if (_enumerator is not null && _notifications is not null)
            {
                try { _enumerator.UnregisterEndpointNotificationCallback(_notifications); }
                catch { /* shutting down anyway */ }
            }

            ComHelpers.Release(_enumerator);
            _enumerator = null;
            _cachedDevices = Array.Empty<AudioDeviceInfo>();
            _cachedDefault = null;
            _cacheValid = true;
        }
    }
}

internal static class ComHelpers
{
    /// <summary>Releases a runtime-callable wrapper without ever throwing at the call site.</summary>
    public static void Release(object? comObject)
    {
        if (comObject is null) return;
        try
        {
            if (Marshal.IsComObject(comObject))
                Marshal.ReleaseComObject(comObject);
        }
        catch
        {
            // Already released, or never a COM object. Nothing useful to do.
        }
    }
}
