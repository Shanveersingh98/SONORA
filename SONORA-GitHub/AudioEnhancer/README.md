# Audio Enhancer (Part 2C - live WASAPI audio)

Local, real-time Windows audio enhancer.
Part 1 = architecture + UI. Part 2A = DSP engine. Part 2B = device detection and the
stream layer. Part 2C = the real WASAPI stream, so audio now flows:

    Windows mix -> loopback capture -> ring buffer -> DSP -> render -> your output device

**The output device must not be the device being captured** - loopback would feed the
enhanced audio straight back into the capture side. Let Windows play to one device and
have the enhancer render to the one you are listening on. See `PART2C.md`.

## Tech
C# / .NET 8 / WPF. Zero NuGet packages: the DSP is plain C# filters and the Windows
audio backend talks to Core Audio through hand-written COM interop.

## Run (Windows 10/11, x64)
1. Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
2. In this folder:  `dotnet run`

## Layout
Three projects, so each layer can be tested without the ones above it.

- `AudioEnhancer/`          the WPF app
  - `src/presets`           IPresetManager + built-in presets
  - `src/ui`                theme, window, view-model
  - `tests/`                the two test suites
- `AudioEnhancer.Dsp/`      (net8.0) EnhancerSettings, IDspEngine, the filters - Part 2A
- `AudioEnhancer.Audio/`    (net8.0-windows) device detection, formats, stream layer - Part 2B
- `tools/AudioEnhancer.DeviceProbe/`  console tool that lists your playback devices

## Tests

    dotnet run --project AudioEnhancer/tests/AudioEnhancer.Dsp.Tests -c Release
    dotnet run --project AudioEnhancer/tests/AudioEnhancer.Audio.Tests -c Release

Or run `build-and-test.cmd` from the solution folder.

## Rule of the architecture
UI  ->  IAudioEngine  ->  IAudioSampleProcessor  ->  IDspEngine.
The UI never touches audio code directly, and the audio backend never references WPF.
