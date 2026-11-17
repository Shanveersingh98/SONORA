# Sonora — Precision Audio Mastering Suite

Sonora is a real-time, low-latency Windows audio enhancement suite and digital signal processing (DSP) engine built with **C#**, **.NET 8**, and **WPF**.

It captures system audio playback via Windows Audio Session API (WASAPI) loopback, applies real-time acoustic mastering filters, and outputs enhanced, crystal-clear sound to your selected playback device with sub-millisecond processing latency.

---

## Key Features

- **10-Band Graphic Equalizer:** Precision frequency shaping from 31 Hz to 16 kHz with smooth parametric biquad curve rendering and negative/positive dB gain scaling (-12 dB to +12 dB).
- **Acoustic Dynamics:**
  - **Bass Boost:** Low-frequency harmonic excitation and punch enhancement without muddying midrange clarity.
  - **Clarity & Air:** High-frequency sheen and detail restoration for vocals and instrument separation.
  - **3D Surround Spatializer:** Psychoacoustic stereo widening and immersive soundstage positioning.
  - **Voice Focus:** Intelligibility boost targeting human speech frequencies for podcasts, movies, and meetings.
- **Master Gain & Limiter:** Output level calibration with lookahead brickwall peak limiting and soft-knee saturation to prevent clipping and digital distortion.
- **Real-Time Spectrum Visualizer:** 30-band dynamic frequency response meter driven by an internal ring buffer and Fourier analysis.
- **Built-In Mastering Presets:**
  - *Studio Reference* (Flat/Neutral)
  - *Bass Monster* (Deep EDM & Hip-Hop profile)
  - *Vocal Clarity* (Podcasts, Dialogue & Audiobook tuning)
  - *Cinematic 3D* (Immersive Movie & Spatial soundstage)
  - *Gaming Edge* (Footstep & positional cue enhancement)
  - *Late Night* (Dynamic range compression & dialogue intelligibility)
  - *Broadcast Pro* (Warm, radio-style presence)
- **Zero Third-Party NuGet Dependencies:** All DSP filtering and Windows Core Audio COM interop are implemented directly from scratch without external dependencies.

---

## Architecture Overview

Sonora is strictly structured into layered, decoupled components:

```
[UI / Presentation]   AudioEnhancer (WPF / MVVM)
         │
         ▼
[Audio Stream Layer]   AudioEnhancer.Audio (WASAPI / Windows Core Audio)
         │
         ▼
[DSP Filter Engine]    AudioEnhancer.Dsp (Biquad IIR Filters / Peak Limiter)
```

1. **`AudioEnhancer/`** — WPF desktop client containing the user interface, custom controls, dark cybernetic theme (`Theme.xaml`), preset management, and `MainViewModel`.
2. **`AudioEnhancer.Audio/`** — Low-latency Windows Core Audio backend:
   - Hand-crafted COM interop (`IMMDeviceEnumerator`, `IAudioClient`, `IAudioCaptureClient`, `IAudioRenderClient`).
   - Lock-free lockless circular ring buffer (`AudioRingBuffer`).
   - High-performance memory stream processing using `Span<float>` and unsafe pointer pinning.
   - Endpoint routing and dynamic device detection (`WindowsOutputDeviceManager`).
3. **`AudioEnhancer.Dsp/`** — Pure .NET 8 DSP filter engine (completely independent from Windows and WPF):
   - Direct Form II Transposed Biquad IIR filters (`Biquad.cs`).
   - Processors: `BassProcessor`, `DetailProcessor`, `EqProcessor`, `GainProcessor`, `Limiter`, `VoiceProcessor`.
4. **`tools/AudioEnhancer.DeviceProbe/`** — Standalone CLI diagnostic tool that enumerates and validates Core Audio playback devices on the host machine.
5. **`AudioEnhancer/tests/`** — Automated unit and integration test suites:
   - `AudioEnhancer.Dsp.Tests`: Numerical precision and filter stability validation.
   - `AudioEnhancer.Audio.Tests`: Core Audio device enumeration and stream loopback verification.
   - `AudioEnhancer.UI.Tests`: ViewModel state machine, preset transitions, and button decrement/increment command testing.

---

## Prerequisites

- **Operating System:** Windows 10 (version 1809+) or Windows 11 (x64)
- **Runtime / SDK:** [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (includes .NET Desktop Runtime 8.0)

---

## Building and Testing

### 1. Build Solution
To compile all projects in Release configuration:
```powershell
dotnet build AudioEnhancer.sln -c Release
```

### 2. Run Test Suites
Run the automated build and test runner script from the root folder:
```cmd
build-and-test.cmd
```
This builds the solution and runs all 217 tests across DSP, Audio, and UI test suites.

Alternatively, execute individual test projects:
```powershell
dotnet run --project AudioEnhancer\tests\AudioEnhancer.Dsp.Tests -c Release
dotnet run --project AudioEnhancer\tests\AudioEnhancer.Audio.Tests -c Release
dotnet run --project AudioEnhancer\tests\AudioEnhancer.UI.Tests -c Release
```

### 3. Run Application from Source
```powershell
dotnet run --project AudioEnhancer -c Release
```

### 4. Publish Single-File Executable
To package a standalone, self-contained single-file `.exe` with all dependencies bundled:
```powershell
dotnet publish AudioEnhancer\AudioEnhancer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o Distribution\Sonora-Release
```

---

## Project Structure

```
SONORA-GitHub/
├── AudioEnhancer.sln                     # Master Visual Studio Solution
├── build-and-test.cmd                    # Automated build & test execution script
├── nuget.config                          # NuGet package configuration
├── .gitignore                            # Standard .NET / WPF repository ignores
├── README.md                             # Repository documentation
├── AudioEnhancer/                        # Main WPF Application
│   ├── app.ico                           # Application icon
│   ├── AudioEnhancer.csproj              # WPF project definition
│   ├── README.md                         # WASAPI stream notes
│   ├── src/                              # Application source code
│   │   ├── App.xaml / App.xaml.cs        # Application lifecycle entry point
│   │   ├── presets/                      # Built-in and custom preset managers
│   │   └── ui/                           # MVVM ViewModel, Views, Controls, Theme
│   └── tests/                            # Test Projects
│       ├── AudioEnhancer.Audio.Tests/    # Windows Audio stream tests
│       ├── AudioEnhancer.Dsp.Tests/      # DSP math & filter stability tests
│       └── AudioEnhancer.UI.Tests/       # UI ViewModel & command tests
├── AudioEnhancer.Audio/                  # Windows Core Audio / WASAPI Library
│   ├── AudioEnhancer.Audio.csproj
│   ├── Analysis/                         # Spectrum analyzer
│   ├── Devices/                          # Endpoint management & routing
│   ├── Diagnostics/                      # Diagnostic logging
│   ├── Format/                           # Sample format converters
│   ├── Interop/                          # Native COM interop interfaces
│   └── Streaming/                        # Lock-free ring buffer & WASAPI streams
├── AudioEnhancer.Dsp/                    # Standalone DSP Processing Engine
│   ├── AudioEnhancer.Dsp.csproj
│   ├── Core/                             # Settings & contracts
│   └── Dsp/                              # Biquad filters, processors & limiter
└── tools/
    └── AudioEnhancer.DeviceProbe/        # Audio device diagnostic CLI tool
```

---

## Important Usage Note (WASAPI Audio Loopback)

To avoid audio feedback loops, the output device selected for Sonora must **not** be the same endpoint currently captured by the Windows system mix. In standard configuration:
1. Allow Windows applications to output to your default virtual or secondary endpoint.
2. Select your physical listening device (Headphones or Speakers) as the active output in Sonora.

---

## License & Authorship

Developed and engineered by **Shanveer**.  
Copyright © 2026 Shanveer. All rights reserved.
