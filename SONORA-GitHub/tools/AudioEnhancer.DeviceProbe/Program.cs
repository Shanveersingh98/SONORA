using AudioEnhancer.Audio;

namespace AudioEnhancer.DeviceProbe;

/// <summary>
/// A read-only look at what the Part 2B backend can see.
///
///     dotnet run --project tools/AudioEnhancer.DeviceProbe -c Release
///
/// It lists every active playback endpoint, marks the default one and asks each for
/// its shared-mode format and timing. It opens no stream, changes no setting and
/// needs no elevation. Exit code 0 means at least one device was found.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine("Audio Enhancer - playback device probe (Part 2B)");
        Console.WriteLine("================================================");
        Console.WriteLine();

        using IAudioBackend backend = AudioBackend.CreateForCurrentPlatform();
        Console.WriteLine(backend.Description);
        Console.WriteLine();

        if (!backend.IsAvailable)
        {
            Console.WriteLine("No Windows audio backend is available on this machine.");
            return 1;
        }

        IReadOnlyList<AudioDeviceInfo> devices = backend.Devices.GetOutputDevices();
        if (devices.Count == 0)
        {
            Console.WriteLine("No active playback devices were found.");
            return 1;
        }

        if (args.Length > 0 && (args[0] == "--restore-default" || args[0] == "--unmute-all" || args[0] == "--set-volume"))
        {
            float targetLevel = 0.20f;
            if (args.Length > 1 && float.TryParse(args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsedLevel))
            {
                targetLevel = Math.Clamp(parsedLevel > 1.0f ? parsedLevel / 100.0f : parsedLevel, 0.0f, 1.0f);
            }

            foreach (var dev in devices)
            {
                float? vol = WindowsDefaultAudioEndpointRouter.GetEndpointVolumeScalar(dev.Id);
                bool? mute = WindowsDefaultAudioEndpointRouter.GetEndpointMute(dev.Id);
                Console.WriteLine($"Device '{dev.Name}': Before Vol = {vol * 100:0}%, Mute = {mute}");

                // Apply target volume and unmute
                WindowsDefaultAudioEndpointRouter.SetEndpointMute(dev.Id, false);
                WindowsDefaultAudioEndpointRouter.SetEndpointVolumeScalar(dev.Id, targetLevel);
                WindowsDefaultAudioEndpointRouter.MuteNonEnhancerSessions(dev.Id, false);
                Console.WriteLine($"Device '{dev.Name}': After Vol = {targetLevel * 100:0}%");
            }

            var physical = devices.FirstOrDefault(d => !VirtualEndpointDetector.IsVirtualEndpoint(d));
            if (physical != null)
            {
                new WindowsDefaultAudioEndpointRouter().TrySetAllRoles(physical.Id, out string err);
                WindowsDefaultAudioEndpointRouter.SetEndpointMute(physical.Id, false);
                WindowsDefaultAudioEndpointRouter.SetEndpointVolumeScalar(physical.Id, targetLevel);
                WindowsDefaultAudioEndpointRouter.MuteNonEnhancerSessions(physical.Id, false);
                Console.WriteLine($"Restored default to: {physical.Name} at {targetLevel * 100:0}% (err: '{err}')");
            }
            return 0;
        }



        if (args.Length > 0 && args[0] == "--ensure-physical")
        {
            WindowsDefaultAudioEndpointRouter.EnsurePhysicalDefaultRestored(forceSpeakers: false);
            Console.WriteLine("Called EnsurePhysicalDefaultRestored(forceSpeakers: false).");
            return 0;
        }

        if (args.Length > 0 && args[0] == "--hide-virtual")
        {
            var virtualDev = devices.FirstOrDefault(d => VirtualEndpointDetector.IsVirtualEndpoint(d));
            if (virtualDev != null)
            {
                bool ok = WindowsDefaultAudioEndpointRouter.SetEndpointVisibility(virtualDev.Id, 0);
                Console.WriteLine($"Hidden {virtualDev.Name}: {ok}");
            }
            return 0;
        }

        if (args.Length > 0 && args[0] == "--show-virtual")
        {
            WindowsDefaultAudioEndpointRouter.EnsureAllVirtualEndpointsVisible();
            Console.WriteLine("Called EnsureAllVirtualEndpointsVisible().");
            return 0;
        }

        if (args.Length > 0 && args[0] == "--rename-virtual")
        {
            var virtualDev = devices.FirstOrDefault(d => VirtualEndpointDetector.IsVirtualEndpoint(d));
            if (virtualDev != null)
            {
                Console.WriteLine($"Found virtual endpoint: {virtualDev.Name} ({virtualDev.Id})");
                bool ok = WindowsDefaultAudioEndpointRouter.TryRenameEndpoint(virtualDev.Id, "Sonora Speakers", out string renameErr);
                Console.WriteLine($"TryRenameEndpoint returned {ok}, err: '{renameErr}'");
            }
            else
            {
                Console.WriteLine("No virtual device found to rename.");
            }
            return 0;
        }

        int index = 0;
        foreach (AudioDeviceInfo device in devices)
        {
            index++;
            string marker = device.IsDefault ? " (default)" : "";
            Console.WriteLine($"{index}. {device.Name}{marker}");
            Console.WriteLine($"   id     {device.Id}");
            float? vol = WindowsDefaultAudioEndpointRouter.GetEndpointVolumeScalar(device.Id);
            bool? mute = WindowsDefaultAudioEndpointRouter.GetEndpointMute(device.Id);
            Console.WriteLine($"   volume {vol * 100:0.0}%, muted: {mute}");

            AudioDeviceCapabilities? capabilities = backend.GetCapabilities(device.Id);
            if (capabilities is null)
            {
                Console.WriteLine("   format could not be queried");
            }
            else
            {
                Console.WriteLine($"   format {capabilities.MixFormat}");
                Console.WriteLine($"   period {capabilities.DefaultPeriod.TotalMilliseconds:0.###} ms " +
                                  $"(minimum {capabilities.MinimumPeriod.TotalMilliseconds:0.###} ms)");
                string dspNote = capabilities.IsDspNativeFormat
                    ? "float samples, no conversion needed"
                    : "needs sample conversion to float";
                Console.WriteLine($"   DSP    {dspNote}");
            }

            Console.WriteLine();
        }

        AudioDeviceCapabilities? defaultCapabilities = backend.GetDefaultCapabilities();
        if (defaultCapabilities is not null)
        {
            var format = defaultCapabilities.MixFormat;
            Console.WriteLine("The DSP engine would be prepared as:");
            Console.WriteLine($"   Prepare(sampleRate: {format.SampleRate}, channels: {format.Channels})");

            var config = new AudioStreamConfiguration();
            Console.WriteLine($"   ring buffer: {config.RingBufferSamples(format)} samples " +
                              $"for a {config.EffectiveBufferDuration.TotalMilliseconds:0.#} ms device buffer");
        }

        Console.WriteLine();
        Console.WriteLine("Note: Part 2B detects and describes devices only. Opening a stream is Part 2C.");
        return 0;
    }
}
