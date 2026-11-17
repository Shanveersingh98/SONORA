using System.Linq;
using AudioEnhancer.Audio;
using AudioEnhancer.Core;
using AudioEnhancer.Presets;

namespace AudioEnhancer.UI.Tests;

public static class Program
{
    public static int Main()
    {
        var runner = new TestRunner();
        Console.WriteLine("AudioEnhancer UI view-model tests (Part 4 Step 6)");
        Console.WriteLine("==================================================");

        CrystalClearTests(runner);
        OutputDeviceSelectionTests(runner);
        StatusErrorTests(runner);
        VirtualEndpointWiringTests(runner);
        ConsumerPresetTests(runner);
        VisualizerTests(runner);
        PowerAndVolumeControlTests(runner);

        return runner.Summarise();
    }

    private static (MainViewModel vm, FakeAudioEngine engine, FakeOutputDeviceManager devices) Build(
        IEnumerable<AudioDeviceInfo>? devices = null)
    {
        var engine = new FakeAudioEngine();
        var deviceManager = new FakeOutputDeviceManager(
            devices ?? new[] { new AudioDeviceInfo("id-speakers", "Speakers", true) });
        var presets = new BuiltInPresetManager();
        var vm = new MainViewModel(engine, deviceManager, presets);
        return (vm, engine, deviceManager);
    }

    // ---------------- Crystal Clear ----------------

    private static void CrystalClearTests(TestRunner runner)
    {
        Console.WriteLine("\n[Crystal Clear]");

        runner.Test("turning Crystal Clear on applies the Step 6 Crystal Clear bundle", () =>
        {
            var (vm, _, _) = Build();

            vm.CrystalClearOn = true;

            TestRunner.AreEqual(vm.Volume.Value, 25, "Volume");
            TestRunner.AreEqual(vm.Bass.Value, 15, "Bass");
            TestRunner.AreEqual(vm.Clarity.Value, 80, "Clarity");
            TestRunner.AreEqual(vm.Voice.Value, 55, "Voice");
            TestRunner.AreEqual(vm.Detail.Value, 70, "Detail");
        });

        runner.Test("turning Crystal Clear off restores the slider values from just before it was turned on", () =>
        {
            var (vm, _, _) = Build();

            // Move away from the startup preset first, so "restore" is meaningfully
            // different from "reset to Balanced" or "reset to zero".
            vm.Volume.Value = 42;
            vm.Bass.Value = 33;
            vm.Clarity.Value = 61;
            vm.Voice.Value = 12;
            vm.Detail.Value = 5;

            vm.CrystalClearOn = true;
            TestRunner.AreEqual(vm.Volume.Value, 25, "Volume while Crystal Clear is on"); // sanity check

            vm.CrystalClearOn = false;

            TestRunner.AreEqual(vm.Volume.Value, 42, "Volume after turning off");
            TestRunner.AreEqual(vm.Bass.Value, 33, "Bass after turning off");
            TestRunner.AreEqual(vm.Clarity.Value, 61, "Clarity after turning off");
            TestRunner.AreEqual(vm.Voice.Value, 12, "Voice after turning off");
            TestRunner.AreEqual(vm.Detail.Value, 5, "Detail after turning off");
        });

        runner.Test("turning Crystal Clear on marks the preset dropdown as Custom", () =>
        {
            var (vm, _, _) = Build();

            vm.CrystalClearOn = true;

            TestRunner.IsTrue(vm.SelectedPreset.IsCustom, "expected the Custom entry to be selected");
        });

        runner.Test("turning Crystal Clear off also leaves the preset dropdown on Custom", () =>
        {
            // Crystal Clear's restored values are not necessarily any named preset's
            // values (in this test they are hand-set slider values), so Custom is still
            // the only honest label for the dropdown once it has been turned off again.
            var (vm, _, _) = Build();

            vm.Volume.Value = 42; // arbitrary, not any preset's value

            vm.CrystalClearOn = true;
            vm.CrystalClearOn = false;

            TestRunner.IsTrue(vm.SelectedPreset.IsCustom, "expected Custom after turning Crystal Clear back off");
        });

        runner.Test("Crystal Clear is never one of the Presets dropdown entries", () =>
        {
            var (vm, _, _) = Build();
            TestRunner.IsTrue(!vm.Presets.Any(p => p.Name == "Crystal Clear"),
                "Crystal Clear must be a separate toggle, not a Presets entry");
        });
    }

    // ---------------- Output device selection ----------------

    private static void OutputDeviceSelectionTests(TestRunner runner)
    {
        Console.WriteLine("\n[Output device selection]");

        runner.Test("selecting an output device calls SetOutputDevice with that device's id", () =>
        {
            var headphones = new AudioDeviceInfo("id-headphones", "USB Headphones", false);
            var (vm, engine, _) = Build(new[]
            {
                new AudioDeviceInfo("id-speakers", "Speakers", true),
                headphones,
            });

            vm.SelectedOutputDevice = headphones;

            TestRunner.AreEqual(engine.SetOutputDeviceCallCount, 1, "SetOutputDevice call count");
            TestRunner.AreEqual(engine.LastOutputDeviceId, "id-headphones", "device id passed to the engine");
        });

        runner.Test("a render device that disappears from Windows' enumeration is cleared, not faked", () =>
        {
            var speakers = new AudioDeviceInfo("id-speakers", "Speakers", true);
            var headphones = new AudioDeviceInfo("id-headphones", "USB Headphones", false);
            var (vm, engine, devices) = Build(new[] { speakers, headphones });

            vm.SelectedOutputDevice = headphones;
            TestRunner.IsTrue(vm.SelectedOutputDevice is not null, "device should be selected before it disappears");

            // The USB headphones are unplugged: Windows now only reports the speakers.
            devices.SetDevices(new[] { speakers });
            vm.RefreshDevicesForTests();

            TestRunner.IsTrue(vm.SelectedOutputDevice is null,
                "a device no longer reported by Windows must never remain selected");
            TestRunner.AreEqual(engine.LastOutputDeviceId, null,
                "the engine must be told to fall back to the default, not left pointed at a stale id");
        });

        runner.Test("a render device that is still present survives a refresh untouched", () =>
        {
            var speakers = new AudioDeviceInfo("id-speakers", "Speakers", true);
            var headphones = new AudioDeviceInfo("id-headphones", "USB Headphones", false);
            var (vm, engine, devices) = Build(new[] { speakers, headphones });

            vm.SelectedOutputDevice = headphones;
            int callsAfterSelection = engine.SetOutputDeviceCallCount;

            devices.SetDevices(new[] { speakers, headphones }); // unchanged
            vm.RefreshDevicesForTests();

            TestRunner.IsTrue(vm.SelectedOutputDevice is not null, "device should still be selected");
            TestRunner.AreEqual(vm.SelectedOutputDevice!.Id, "id-headphones", "selected device id");
            TestRunner.AreEqual(engine.SetOutputDeviceCallCount, callsAfterSelection,
                "a refresh that changes nothing must not call SetOutputDevice again");
        });

        runner.Test("headphone connection updates IsHeadphonesConnected and visibility badges", () =>
        {
            var speakers = new AudioDeviceInfo("id-speakers", "Speakers", true);
            var (vm, _, devices) = Build(new[] { speakers });

            TestRunner.IsTrue(!vm.IsHeadphonesConnected, "should not report headphones when only speakers are present");
            TestRunner.IsTrue(vm.HeadphonesBadgeVisibility == System.Windows.Visibility.Collapsed, "headphone badge collapsed");
            TestRunner.IsTrue(vm.SpeakersIconVisibility == System.Windows.Visibility.Visible, "speaker icon visible");
            TestRunner.IsTrue(vm.HeadphonesIconVisibility == System.Windows.Visibility.Collapsed, "headphone icon collapsed");

            var headphones = new AudioDeviceInfo("id-hp", "Realtek Headphones", false);
            devices.SetDevices(new[] { speakers, headphones });
            vm.RefreshDevicesForTests();

            TestRunner.IsTrue(vm.IsHeadphonesConnected, "should report headphones when headphones are connected");
            TestRunner.IsTrue(vm.HeadphonesBadgeVisibility == System.Windows.Visibility.Visible, "headphone badge visible");
            TestRunner.IsTrue(vm.SpeakersIconVisibility == System.Windows.Visibility.Collapsed, "speaker icon collapsed");
            TestRunner.IsTrue(vm.HeadphonesIconVisibility == System.Windows.Visibility.Visible, "headphone icon visible");
        });

        runner.Test("virtual default output resolves physical device name for the user display", () =>
        {
            var virtualCable = new AudioDeviceInfo("id-virt", "Speakers (AudioEnhancer Virtual Output)", true);
            var realSpeakers = new AudioDeviceInfo("id-real", "Realtek High Definition Audio", false);
            var (vm, _, _) = Build(new[] { virtualCable, realSpeakers });

            TestRunner.AreEqual(vm.OutputDeviceName, "Realtek High Definition Audio",
                "route display should show physical target instead of virtual cable");
        });
    }

    // ---------------- Status error state ----------------

    private static void StatusErrorTests(TestRunner runner)
    {
        Console.WriteLine("\n[Status error state]");

        runner.Test("IsStatusError is true when the engine has faulted", () =>
        {
            var (vm, engine, _) = Build();
            engine.State = EngineState.Faulted;

            TestRunner.IsTrue(vm.IsStatusError, "expected IsStatusError to be true while EngineState.Faulted");
        });

        runner.Test("IsStatusError is false for every non-faulted engine state", () =>
        {
            var (vm, engine, _) = Build();

            foreach (EngineState state in new[]
                     {
                         EngineState.NotAvailable, EngineState.Stopped, EngineState.Starting, EngineState.Running,
                     })
            {
                engine.State = state;
                TestRunner.IsTrue(!vm.IsStatusError, $"expected IsStatusError to be false for {state}");
            }
        });
    }

    // ---------------- Virtual endpoint wiring (Part 4 Step 7, no-WDK application prep) ----------------
    //
    // These tests exercise how MainViewModel reaches IAudioEngine.GetVirtualEndpointStatus /
    // TryUseVirtualEndpointForCapture - the same "wire the interface through to the view
    // model" work this file already does for SetOutputDevice and IsStatusError. They do not,
    // and cannot, test a real driver: FakeAudioEngine's VirtualEndpointStatus/CaptureOutcome
    // are set directly by the test, exactly like every other FakeAudioEngine member.

    private static void VirtualEndpointWiringTests(TestRunner runner)
    {
        Console.WriteLine("\n[Virtual endpoint wiring (no-WDK application prep)]");

        runner.Test("VirtualEndpointStatusText reflects whatever the engine currently reports", () =>
        {
            var (vm, engine, _) = Build();
            engine.VirtualEndpointStatus = new VirtualEndpointStatusReport(
                AudioEnhancer.Audio.VirtualEndpointStatus.NotInstalled, Device: null,
                Message: "not installed - no driver exists yet");

            TestRunner.AreEqual(vm.VirtualEndpointStatusText, "not installed - no driver exists yet", "status text");
        });

        runner.Test("VirtualEndpointStatusText is read live, not cached, when the engine's report changes", () =>
        {
            // Same reasoning the Step 6 IsStatusError tests already apply: nothing here
            // should require a Refresh() call in between to pick up the engine's new answer.
            var (vm, engine, _) = Build();
            var installedDevice = new AudioDeviceInfo("id-virtual", "AudioEnhancer Virtual Output", false);

            engine.VirtualEndpointStatus = new VirtualEndpointStatusReport(
                AudioEnhancer.Audio.VirtualEndpointStatus.InstalledNotDefault, installedDevice, "installed, not default");

            TestRunner.AreEqual(vm.VirtualEndpointStatusText, "installed, not default", "status text after change");
            TestRunner.IsTrue(vm.IsVirtualEndpointInstalled, "expected IsVirtualEndpointInstalled to follow the report");
        });

        runner.Test("IsVirtualEndpointInstalled is false while the engine reports NotInstalled", () =>
        {
            var (vm, engine, _) = Build();
            engine.VirtualEndpointStatus = new VirtualEndpointStatusReport(
                AudioEnhancer.Audio.VirtualEndpointStatus.NotInstalled, Device: null, Message: "not installed");

            TestRunner.IsTrue(!vm.IsVirtualEndpointInstalled, "expected false while NotInstalled");
        });

        runner.Test("UseVirtualOutputForCapture calls through to the engine exactly once", () =>
        {
            var (vm, engine, _) = Build();

            vm.UseVirtualOutputForCapture();

            TestRunner.AreEqual(engine.TryUseVirtualEndpointForCaptureCallCount, 1, "call count");
        });

        runner.Test("UseVirtualOutputForCapture refreshes VirtualEndpointStatusText from the engine afterwards", () =>
        {
            // Simulates the realistic sequence: before the call the endpoint is not
            // installed; the fake stands in for "the driver is now installed" by changing
            // what GetVirtualEndpointStatus() will report on the next read, and the view
            // model must reflect that without any extra wiring from the test.
            var (vm, engine, _) = Build();
            engine.VirtualEndpointStatus = new VirtualEndpointStatusReport(
                AudioEnhancer.Audio.VirtualEndpointStatus.NotInstalled, Device: null, Message: "before");

            engine.VirtualEndpointCaptureOutcome = new VirtualEndpointCaptureOutcome(
                VirtualEndpointCaptureResult.NotInstalled, "capture attempt result, not shown by this property");

            vm.UseVirtualOutputForCapture();
            TestRunner.AreEqual(vm.VirtualEndpointStatusText, "before", "status text unchanged when nothing installed");

            var installedDevice = new AudioDeviceInfo("id-virtual", "AudioEnhancer Virtual Output", false);
            engine.VirtualEndpointStatus = new VirtualEndpointStatusReport(
                AudioEnhancer.Audio.VirtualEndpointStatus.InstalledAndDefault, installedDevice, "after");

            vm.UseVirtualOutputForCapture();
            TestRunner.AreEqual(vm.VirtualEndpointStatusText, "after", "status text after the engine's report changes");
        });
    }

    // ---------------- Consumer Presets ----------------

    private static void ConsumerPresetTests(TestRunner runner)
    {
        Console.WriteLine("\n[Consumer Presets]");

        runner.Test("all 8 consumer presets exist in Presets list", () =>
        {
            var (vm, _, _) = Build();
            TestRunner.AreEqual(vm.Presets.Count, 8, "preset count");
            TestRunner.IsTrue(vm.Presets.Any(p => p.Name == "Music"), "Music preset present");
            TestRunner.IsTrue(vm.Presets.Any(p => p.Name == "Gaming"), "Gaming preset present");
            TestRunner.IsTrue(vm.Presets.Any(p => p.Name == "Movies"), "Movies preset present");
            TestRunner.IsTrue(vm.Presets.Any(p => p.Name == "Voice"), "Voice preset present");
            TestRunner.IsTrue(vm.Presets.Any(p => p.Name == "Bass Boost"), "Bass Boost preset present");
            TestRunner.IsTrue(vm.Presets.Any(p => p.Name == "Loud"), "Loud preset present");
            TestRunner.IsTrue(vm.Presets.Any(p => p.Name == "Balanced"), "Balanced preset present");
            TestRunner.IsTrue(vm.Presets.Any(p => p.Name == "Custom"), "Custom preset present");
        });

        runner.Test("selecting Loud preset applies maximum clean loudness settings", () =>
        {
            var (vm, _, _) = Build();
            var loud = vm.Presets.First(p => p.Name == "Loud");
            vm.SelectedPreset = loud;

            TestRunner.AreEqual(vm.Volume.Value, 85, "Loud volume");
            TestRunner.AreEqual(vm.Clarity.Value, 70, "Loud clarity");
            TestRunner.AreEqual(vm.Detail.Value, 60, "Loud detail");
        });

        runner.Test("selecting Music preset applies balanced music enhancement settings", () =>
        {
            var (vm, _, _) = Build();
            var music = vm.Presets.First(p => p.Name == "Music");
            vm.SelectedPreset = music;

            TestRunner.AreEqual(vm.Volume.Value, 50, "Music volume");
            TestRunner.AreEqual(vm.Bass.Value, 50, "Music bass");
            TestRunner.AreEqual(vm.Clarity.Value, 60, "Music clarity");
        });

        runner.Test("moving a slider switches preset back to Custom", () =>
        {
            var (vm, _, _) = Build();
            var music = vm.Presets.First(p => p.Name == "Music");
            vm.SelectedPreset = music;
            TestRunner.AreEqual(vm.SelectedPreset.Name, "Music", "preset is Music");

            vm.Volume.Value = 92;
            TestRunner.IsTrue(vm.SelectedPreset.IsCustom, "preset changed to Custom on slider adjustment");
        });
    }

    // ---------------- Visual Frequency Equalizer Spectrum ----------------

    private static void VisualizerTests(TestRunner runner)
    {
        Console.WriteLine("\n[Visual Frequency Equalizer Spectrum]");

        runner.Test("frequency visualizer initializes with 11 frequency bands from 20 Hz to 20 kHz", () =>
        {
            var (vm, _, _) = Build();
            TestRunner.AreEqual(vm.FrequencyBands.Count, 11, "bands count");
            TestRunner.AreEqual(vm.FrequencyBands[0].Label, "20", "first band label");
            TestRunner.AreEqual(vm.FrequencyBands[10].Label, "20k", "last band label");
        });

        runner.Test("bass slider increase dynamically raises sub-bass and bass frequency bands", () =>
        {
            var (vm, _, _) = Build();
            vm.EnhancementOn = true;

            vm.Bass.Value = 0;
            double lowBefore = vm.FrequencyBands[1].Height;

            vm.Bass.Value = 100;
            double lowAfter = vm.FrequencyBands[1].Height;

            TestRunner.IsTrue(lowAfter > lowBefore, "50 Hz band height increased with Bass");
        });

        runner.Test("turning enhancement OFF flattens all frequency visualizer bands to baseline", () =>
        {
            var (vm, _, _) = Build();
            vm.EnhancementOn = true;
            vm.Bass.Value = 100;

            vm.EnhancementOn = false;
            for (int i = 0; i < vm.FrequencyBands.Count; i++)
            {
                TestRunner.AreEqual(vm.FrequencyBands[i].Height, 16.0, $"band {i} flat baseline on bypass");
            }
        });

        runner.Test("hardware mute key (F3) displays MUTE and flattens frequency bands to 12.0", () =>
        {
            var (vm, engine, _) = Build();
            vm.EnhancementOn = true;
            vm.Volume.Value = 80;

            TestRunner.IsTrue(vm.GainDbReadout.Contains("dB"), "unmuted readout displays dB");

            // Simulate laptop F3 hardware mute key press
            vm.Volume.Value = 0;
            TestRunner.IsTrue(vm.IsMuted, "0 volume is muted");
            TestRunner.AreEqual(vm.GainDbReadout, "MUTE", "GainDbReadout shows MUTE");

            for (int i = 0; i < vm.FrequencyBands.Count; i++)
            {
                TestRunner.AreEqual(vm.FrequencyBands[i].Height, 12.0, $"band {i} clamped to 12.0 when muted");
            }
        });

        runner.Test("hardware volume decrease synchronizes master volume to physical output", () =>
        {
            var (vm, engine, _) = Build();
            vm.EnhancementOn = true;
            vm.Volume.Value = 75;

            // User lowers volume slider to 20%
            vm.Volume.Value = 20;
            TestRunner.IsTrue(MathF.Abs(engine.MasterVolume.GetValueOrDefault() - 0.20f) < 0.001f, "physical master volume synchronized to 0.20");
            TestRunner.AreEqual(vm.GainDbReadout, "+7.2 dB", "readout reflects +7.2 dB at volume 20");
        });
    }

    // ---------------- Big Power Switch & Master Volume Controls ----------------

    private static void PowerAndVolumeControlTests(TestRunner runner)
    {
        Console.WriteLine("\n[Big Power Switch & Master Volume Controls]");

        runner.Test("TogglePowerCommand toggles EnhancementOn and updates power button text", () =>
        {
            var (vm, _, _) = Build();
            TestRunner.IsFalse(vm.EnhancementOn, "initial enhancement is off in test");
            TestRunner.AreEqual(vm.EnhancementPowerButtonText, "MASTER ENHANCEMENT [ OFF ]", "off power button text");

            vm.TogglePowerCommand.Execute(null);
            TestRunner.IsTrue(vm.EnhancementOn, "enhancement toggled on");
            TestRunner.AreEqual(vm.EnhancementPowerButtonText, "MASTER ENHANCEMENT [ ON ]", "on power button text");
            TestRunner.AreEqual(vm.EnhancementPowerSubtext, "Studio DSP Processing & Limiter Active", "on power subtext");

            vm.TogglePowerCommand.Execute(null);
            TestRunner.IsFalse(vm.EnhancementOn, "enhancement toggled off");
            TestRunner.AreEqual(vm.EnhancementPowerButtonText, "MASTER ENHANCEMENT [ OFF ]", "off power button text restored");
        });

        runner.Test("VolumeUpCommand increases volume by 5% and syncs engine master volume", () =>
        {
            var (vm, engine, _) = Build();
            vm.Volume.Value = 50;
            vm.VolumeUpCommand.Execute(null);
            TestRunner.AreEqual(vm.Volume.Value, 55, "volume increased to 55");
            TestRunner.IsTrue(MathF.Abs(engine.MasterVolume.GetValueOrDefault() - 0.55f) < 0.001f, "engine master volume synced to 0.55");
        });

        runner.Test("VolumeDownCommand decreases volume by 5% and syncs engine master volume", () =>
        {
            var (vm, engine, _) = Build();
            vm.Volume.Value = 50;
            vm.VolumeDownCommand.Execute(null);
            TestRunner.AreEqual(vm.Volume.Value, 45, "volume decreased to 45");
            TestRunner.IsTrue(MathF.Abs(engine.MasterVolume.GetValueOrDefault() - 0.45f) < 0.001f, "engine master volume synced to 0.45");
        });

        runner.Test("SetVolumeStepCommand sets volume to specified step values and syncs engine master volume", () =>
        {
            var (vm, engine, _) = Build();
            vm.SetVolumeStepCommand.Execute(25);
            TestRunner.AreEqual(vm.Volume.Value, 25, "volume step 25");
            TestRunner.IsTrue(MathF.Abs(engine.MasterVolume.GetValueOrDefault() - 0.25f) < 0.001f, "engine master volume synced to 0.25");

            vm.SetVolumeStepCommand.Execute(50);
            TestRunner.AreEqual(vm.Volume.Value, 50, "volume step 50");
            TestRunner.IsTrue(MathF.Abs(engine.MasterVolume.GetValueOrDefault() - 0.50f) < 0.001f, "engine master volume synced to 0.50");

            vm.SetVolumeStepCommand.Execute(75);
            TestRunner.AreEqual(vm.Volume.Value, 75, "volume step 75");
            TestRunner.IsTrue(MathF.Abs(engine.MasterVolume.GetValueOrDefault() - 0.75f) < 0.001f, "engine master volume synced to 0.75");

            vm.SetVolumeStepCommand.Execute(100);
            TestRunner.AreEqual(vm.Volume.Value, 100, "volume step 100");
            TestRunner.IsTrue(MathF.Abs(engine.MasterVolume.GetValueOrDefault() - 1.0f) < 0.001f, "engine master volume synced to 1.00");

            // Test auto-mute at step 0
            vm.SetVolumeStepCommand.Execute(0);
            TestRunner.AreEqual(vm.Volume.Value, 0, "volume step 0");
            TestRunner.IsTrue(vm.IsMuted, "is muted at volume 0");
            TestRunner.IsTrue(engine.MasterMute.GetValueOrDefault(), "engine muted at volume 0");

            // Test auto-unmute when stepping up
            vm.SetVolumeStepCommand.Execute(50);
            TestRunner.IsFalse(vm.IsMuted, "unmuted at volume 50");
            TestRunner.IsFalse(engine.MasterMute.GetValueOrDefault(), "engine unmuted at volume 50");
        });

        runner.Test("ToggleMuteCommand toggles mute state and restores loudness readout", () =>
        {
            var (vm, engine, _) = Build();
            vm.Volume.Value = 75;
            TestRunner.IsFalse(vm.IsMuted, "initially unmuted");
            TestRunner.AreEqual(vm.MuteButtonText, "MUTE", "mute button text when unmuted");

            vm.ToggleMuteCommand.Execute(null);
            TestRunner.IsTrue(vm.IsMuted, "muted after toggle");
            TestRunner.AreEqual(vm.MuteButtonText, "UNMUTE", "mute button text when muted");
            TestRunner.AreEqual(vm.GainDbReadout, "MUTE", "readout reflects MUTE");

            vm.ToggleMuteCommand.Execute(null);
            TestRunner.IsFalse(vm.IsMuted, "unmuted after second toggle");
            TestRunner.AreEqual(vm.MuteButtonText, "MUTE", "mute button text restored");
        });

        runner.Test("Fader step commands decrement and increment by 5%", () =>
        {
            var (vm, _, _) = Build();
            vm.Bass.Value = 50;
            vm.BassDownCommand.Execute(null);
            TestRunner.AreEqual(vm.Bass.Value, 45, "bass decreased by 5");
            vm.BassUpCommand.Execute(null);
            TestRunner.AreEqual(vm.Bass.Value, 50, "bass increased by 5");

            vm.Clarity.Value = 30;
            vm.ClarityDownCommand.Execute(null);
            TestRunner.AreEqual(vm.Clarity.Value, 25, "clarity decreased by 5");
            vm.ClarityUpCommand.Execute(null);
            TestRunner.AreEqual(vm.Clarity.Value, 30, "clarity increased by 5");

            vm.Detail.Value = 40;
            vm.DetailDownCommand.Execute(null);
            TestRunner.AreEqual(vm.Detail.Value, 35, "detail decreased by 5");
            vm.DetailUpCommand.Execute(null);
            TestRunner.AreEqual(vm.Detail.Value, 40, "detail increased by 5");

            vm.Voice.Value = 20;
            vm.VoiceDownCommand.Execute(null);
            TestRunner.AreEqual(vm.Voice.Value, 15, "voice decreased by 5");
            vm.VoiceUpCommand.Execute(null);
            TestRunner.AreEqual(vm.Voice.Value, 20, "voice increased by 5");
        });

        runner.Test("SetVolumeStepCommand accepts string parameters from XAML", () =>
        {
            var (vm, _, _) = Build();
            vm.SetVolumeStepCommand.Execute("25");
            TestRunner.AreEqual(vm.Volume.Value, 25, "volume step 25 from string");

            vm.SetVolumeStepCommand.Execute("75");
            TestRunner.AreEqual(vm.Volume.Value, 75, "volume step 75 from string");
        });

        runner.Test("FrequencyBandItem supports negative dB values safely", () =>
        {
            var item = new AudioEnhancer.UI.FrequencyBandItem("1k", 16.0);
            TestRunner.AreEqual(item.GainDb, 0.0, "initial 16.0 height is 0 dB");
            item.SliderValue = -6.0;
            TestRunner.AreEqual(item.GainDb, -6.0, "slider value -6.0 maps to -6.0 dB");
            TestRunner.AreEqual(item.GainDbText, "-6.0", "gain text shows negative value");
            TestRunner.IsTrue(item.Height >= 0, "height is never negative");
        });
    }
}
