using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using AudioEnhancer.Audio;
using AudioEnhancer.Core;
using AudioEnhancer.Dsp;
using AudioEnhancer.Presets;

namespace AudioEnhancer.UI;

/// <summary>
/// A single frequency bar item for the visual EQ display (20 Hz to 20 kHz).
/// </summary>
public sealed class FrequencyBandItem : ViewModelBase
{
    private double _height;
    private double _gainDb;
    private readonly Action<int, double>? _onUserAdjust;
    private readonly int _bandIndex;

    public FrequencyBandItem(string label, double initialHeight, int bandIndex = 0, Action<int, double>? onUserAdjust = null)
    {
        Label = label;
        _bandIndex = bandIndex;
        _onUserAdjust = onUserAdjust;
        _height = initialHeight;
        _gainDb = Math.Round(Math.Clamp((initialHeight - 16.0) / 4.0, -12.0, 12.0), 1);
    }

    public string Label { get; }
    public int BandIndex => _bandIndex;

    public double Height
    {
        get => _height;
        set
        {
            if (SetProperty(ref _height, value))
            {
                _gainDb = Math.Round(Math.Clamp((_height - 16.0) / 4.0, -12.0, 12.0), 1);
                OnPropertyChanged(nameof(GainDb));
                OnPropertyChanged(nameof(GainDbText));
                OnPropertyChanged(nameof(SliderValue));
            }
        }
    }

    public double GainDb
    {
        get => _gainDb;
        set
        {
            double clamped = Math.Round(Math.Clamp(value, -12.0, 12.0), 1);
            if (SetProperty(ref _gainDb, clamped))
            {
                _height = Math.Max(2.0, 16.0 + (_gainDb * 4.0));
                OnPropertyChanged(nameof(Height));
                OnPropertyChanged(nameof(GainDbText));
                OnPropertyChanged(nameof(SliderValue));
            }
        }
    }

    public string GainDbText => _gainDb > 0 ? $"+{_gainDb:0.0}" : (_gainDb < 0 ? $"{_gainDb:0.0}" : "0.0");

    public double SliderValue
    {
        get => _gainDb;
        set
        {
            if (Math.Abs(_gainDb - value) > 0.05)
            {
                GainDb = value;
                _onUserAdjust?.Invoke(_bandIndex, _gainDb);
            }
        }
    }
}

/// <summary>
/// Consumer-grade UI ViewModel separating sound and screen.
/// Provides one-click presets, dynamic EQ curve visualization, and seamless DSP bypass.
/// </summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly IAudioEngine _engine;
    private readonly IOutputDeviceManager _devices;
    private readonly IPresetManager _presets;
    private readonly DispatcherTimer? _visualizerTimer;
    private int _visualizerTick;
    private bool _applyingPreset;
    private bool _enhancementOn;
    private Preset _selectedPreset;
    private string _statusText = "";
    private string _processingText = "";
    private string _outputDeviceName = "";
    private bool _crystalClearOn;
    private EnhancerSettings? _preCrystalClearSettings;
    private AudioDeviceInfo? _selectedOutputDevice;

    public System.Windows.Input.ICommand TogglePowerCommand { get; }
    public System.Windows.Input.ICommand VolumeUpCommand { get; }
    public System.Windows.Input.ICommand VolumeDownCommand { get; }
    public System.Windows.Input.ICommand BassUpCommand { get; }
    public System.Windows.Input.ICommand BassDownCommand { get; }
    public System.Windows.Input.ICommand ClarityUpCommand { get; }
    public System.Windows.Input.ICommand ClarityDownCommand { get; }
    public System.Windows.Input.ICommand DetailUpCommand { get; }
    public System.Windows.Input.ICommand DetailDownCommand { get; }
    public System.Windows.Input.ICommand VoiceUpCommand { get; }
    public System.Windows.Input.ICommand VoiceDownCommand { get; }
    public System.Windows.Input.ICommand SetVolumeStepCommand { get; }
    public System.Windows.Input.ICommand ToggleMuteCommand { get; }
    public System.Windows.Input.ICommand ResetDefaultsCommand { get; }
    public System.Windows.Input.ICommand FlattenEqCommand { get; }
    public System.Windows.Input.ICommand AcousticCurveCommand { get; }
    public System.Windows.Input.ICommand SelectPresetCommand { get; }
    private bool _syncingFromSystem;
    private long _lastUserVolumeChangeTick;
    private bool _isUserAdjustingEq;

    public MainViewModel(IAudioEngine engine, IOutputDeviceManager devices, IPresetManager presets)
    {
        _engine = engine;
        _devices = devices;
        _presets = presets;

        var start = presets.Default.Settings;

        // Query current system volume so Sonora opens at the user's actual laptop listening level
        float? currentSysVol = _engine.GetMasterVolume();
        int initialVol = currentSysVol.HasValue && currentSysVol.Value >= 0.50f
            ? (int)Math.Clamp(Math.Round(currentSysVol.Value * 100.0), 0, 100)
            : start.Volume;

        bool? currentSysMute = _engine.GetMasterMute();
        _isMuted = currentSysMute ?? false;

        Volume  = new SliderControl("Loudness Boost", initialVol);
        Bass    = new SliderControl("Bass Boost", start.Bass);
        Clarity = new SliderControl("Clarity", start.Clarity);
        Voice   = new SliderControl("Vocal Presence", start.Voice);
        Detail  = new SliderControl("Detail & Air", start.Detail);
        Sliders = new[] { Volume, Bass, Clarity, Voice, Detail };

        _selectedPreset = presets.Default;
        _outputDeviceName = string.Empty;
        RefreshDevices();

        TogglePowerCommand = new RelayCommand(() => EnhancementOn = !EnhancementOn);
        VolumeUpCommand = new RelayCommand(() => AdjustVolume(+5));
        VolumeDownCommand = new RelayCommand(() => AdjustVolume(-5));
        BassDownCommand = new RelayCommand(() => AdjustFader(Bass, -5));
        BassUpCommand = new RelayCommand(() => AdjustFader(Bass, +5));
        ClarityDownCommand = new RelayCommand(() => AdjustFader(Clarity, -5));
        ClarityUpCommand = new RelayCommand(() => AdjustFader(Clarity, +5));
        DetailDownCommand = new RelayCommand(() => AdjustFader(Detail, -5));
        DetailUpCommand = new RelayCommand(() => AdjustFader(Detail, +5));
        VoiceDownCommand = new RelayCommand(() => AdjustFader(Voice, -5));
        VoiceUpCommand = new RelayCommand(() => AdjustFader(Voice, +5));
        SetVolumeStepCommand = new RelayCommand(p =>
        {
            int val = p switch
            {
                int step => step,
                string s when int.TryParse(s, out int parsed) => parsed,
                _ => -1
            };
            if (val >= 0)
            {
                _lastUserVolumeChangeTick = Environment.TickCount64;
                Volume.Value = Math.Clamp(val, 0, 100);
                SelectedPreset = _presets.Custom;
                PushSettings();
            }
        });
        ToggleMuteCommand = new RelayCommand(() =>
        {
            _isMuted = !_isMuted;
            _engine.SetMasterMute(_isMuted);
            OnPropertyChanged(nameof(IsMuted));
            OnPropertyChanged(nameof(GainDbReadout));
            OnPropertyChanged(nameof(LoudnessText));
            OnPropertyChanged(nameof(LoudnessDbUnit));
            OnPropertyChanged(nameof(MuteButtonText));
        });
        ResetDefaultsCommand = new RelayCommand(() =>
        {
            ApplySliderValues(_presets.Default.Settings, preserveMasterVolume: _engine is WasapiAudioEngine);
            SelectedPreset = _presets.Default;
            PushSettings();
        });
        FlattenEqCommand = new RelayCommand(() =>
        {
            Bass.Value = 0;
            Clarity.Value = 0;
            Voice.Value = 0;
            Detail.Value = 0;
            SelectedPreset = _presets.Custom;
            for (int i = 0; i < FrequencyBands.Count; i++)
            {
                FrequencyBands[i].Height = 16.0;
            }
            PushSettings();
            UpdateEqCurve();
        });
        AcousticCurveCommand = new RelayCommand(() =>
        {
            Bass.Value = 65;
            Clarity.Value = 45;
            Voice.Value = 25;
            Detail.Value = 60;
            SelectedPreset = _presets.Custom;
            PushSettings();
        });
        SelectPresetCommand = new RelayCommand(p =>
        {
            if (p is Preset preset) SelectedPreset = preset;
        });

        // Initialize the 11 frequency bands (20 Hz to 20 kHz)
        FrequencyBands = new ObservableCollection<FrequencyBandItem>
        {
            new("20", 18.0, 0, OnEqBandAdjusted),
            new("50", 22.0, 1, OnEqBandAdjusted),
            new("100", 20.0, 2, OnEqBandAdjusted),
            new("250", 18.0, 3, OnEqBandAdjusted),
            new("500", 18.0, 4, OnEqBandAdjusted),
            new("1k", 20.0, 5, OnEqBandAdjusted),
            new("2k", 22.0, 6, OnEqBandAdjusted),
            new("4k", 22.0, 7, OnEqBandAdjusted),
            new("8k", 24.0, 8, OnEqBandAdjusted),
            new("16k", 26.0, 9, OnEqBandAdjusted),
            new("20k", 22.0, 10, OnEqBandAdjusted),
        };

        // Subscribe to changes after initial setup
        foreach (var s in Sliders) s.PropertyChanged += OnSliderChanged;
        _engine.StateChanged += OnEngineStateChanged;
        _devices.DefaultOutputChanged += OnDefaultOutputChanged;
        _devices.DevicesChanged += OnDevicesChanged;

        UpdateDialArc();
        UpdateEqCurve();

        // Visualizer animation timer (runs only in live WPF UI)
        if (Application.Current != null)
        {
            _visualizerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45) };
            _visualizerTimer.Tick += OnVisualizerTick;
            _visualizerTimer.Start();
        }

        UpdateFrequencyResponse();
        Refresh();
    }

    public SliderControl Volume { get; }
    public SliderControl Bass { get; }
    public SliderControl Clarity { get; }
    public SliderControl Voice { get; }
    public SliderControl Detail { get; }
    public IReadOnlyList<SliderControl> Sliders { get; }

    public IReadOnlyList<Preset> Presets => _presets.Presets;
    public ObservableCollection<FrequencyBandItem> FrequencyBands { get; }

    // Real-Time 32-Band FFT Audio Spectrum (pre-allocated reusable arrays)
    private readonly float[] _spectrumBands = new float[32];
    public IReadOnlyList<float> SpectrumBands => _spectrumBands;

    private float _peakDb = -120f;
    public float PeakDb { get => _peakDb; private set => SetProperty(ref _peakDb, value); }
    public string PeakDbText => _peakDb <= -90f ? "-∞ dB" : $"{_peakDb:+0.0;-0.0;0.0} dB";

    private float _rmsDb = -120f;
    public float RmsDb { get => _rmsDb; private set => SetProperty(ref _rmsDb, value); }
    public string RmsDbText => _rmsDb <= -90f ? "-∞ dB" : $"{_rmsDb:+0.0;-0.0;0.0} dB";

    private bool _isAudioActive;
    public bool IsAudioActive
    {
        get => _isAudioActive;
        private set
        {
            if (SetProperty(ref _isAudioActive, value))
            {
                OnPropertyChanged(nameof(AudioActiveText));
                OnPropertyChanged(nameof(AudioSignalText));
            }
        }
    }

    public string AudioActiveText => IsAudioActive ? "AUDIO ACTIVE" : "WAITING FOR AUDIO";
    public string AudioSignalText => IsAudioActive ? "LIVE" : "IDLE";
    public string DynamicHeadroomText => _peakDb <= -90f ? "-- dB" : $"{Math.Max(0f, -_peakDb):+0.0;-0.0;0.0} dB";

    public string LoudnessText => IsMuted ? "MUTE" : $"+{SliderMap.VolumeDb((int)Volume.Value):0.0}";
    public string LoudnessDbUnit => IsMuted ? "" : "dB";

    public string BassDbText => $"+{Bass.Value * 0.12:0.0} dB";
    public string ClarityDbText => $"+{Clarity.Value * 0.10:0.0} dB";
    public string SurroundText => $"{Detail.Value:0}%";
    public string DrcText => $"Auto -{Voice.Value * 0.10:0.0} dB";

    public string SelectedPresetName => SelectedPreset?.Name ?? "Balanced";
    public string ActivePresetHeader => $"{SelectedPreset?.Name?.ToUpperInvariant() ?? "BALANCED"} (OPTIMIZED)";

    private double _knobRotationAngle = 0.0;
    public double KnobRotationAngle
    {
        get => _knobRotationAngle;
        private set => SetProperty(ref _knobRotationAngle, value);
    }

    private string _dialArcPathData = "M 33.3,126.7 A 66,66 0 0 1 33.4,126.7";
    public string DialArcPathData
    {
        get => _dialArcPathData;
        private set => SetProperty(ref _dialArcPathData, value);
    }

    private string _spectrumLinePathData = "M 0,154 L 800,154";
    public string SpectrumLinePathData
    {
        get => _spectrumLinePathData;
        private set => SetProperty(ref _spectrumLinePathData, value);
    }

    private string _spectrumFillPathData = "M 0,154 L 800,154 L 800,160 L 0,160 Z";
    public string SpectrumFillPathData
    {
        get => _spectrumFillPathData;
        private set => SetProperty(ref _spectrumFillPathData, value);
    }

    private string _spectrumBarsPathData = "";
    public string SpectrumBarsPathData
    {
        get => _spectrumBarsPathData;
        private set => SetProperty(ref _spectrumBarsPathData, value);
    }

    private string _eqCurvePathData = "M 0,20 L 100,20";
    public string EqCurvePathData
    {
        get => _eqCurvePathData;
        private set => SetProperty(ref _eqCurvePathData, value);
    }

    /// <summary>Name of the playback device Windows is currently defaulting to.</summary>
    public string OutputDeviceName { get => _outputDeviceName; private set => SetProperty(ref _outputDeviceName, value); }

    /// <summary>Every active playback device, filtered to physical devices only.</summary>
    public IReadOnlyList<AudioDeviceInfo> OutputDevices =>
        _devices.GetOutputDevices()
            .Where(d => !VirtualEndpointDetector.IsVirtualEndpoint(d))
            .ToList();

    public bool IsHeadphonesConnected
    {
        get
        {
            if (_selectedOutputDevice is not null)
                return _selectedOutputDevice.IsHeadphones;
            return OutputDevices.Any(d => d.IsHeadphones);
        }
    }

    public Visibility HeadphonesBadgeVisibility => IsHeadphonesConnected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SpeakersIconVisibility => IsHeadphonesConnected ? Visibility.Collapsed : Visibility.Visible;
    public Visibility HeadphonesIconVisibility => IsHeadphonesConnected ? Visibility.Visible : Visibility.Collapsed;

    public string EnhancementLabel => _enhancementOn ? "ON" : "OFF";
    public string EnhancementBtnLabel => _enhancementOn ? "ENHANCEMENT: ON" : "ENHANCEMENT: OFF";
    public string EnhancementPowerButtonText => _enhancementOn ? "MASTER ENHANCEMENT [ ON ]" : "MASTER ENHANCEMENT [ OFF ]";
    public string EnhancementPowerSubtext => _enhancementOn ? "Studio DSP Processing & Limiter Active" : "DSP Bypassed • Normal Laptop Audio";
    public string MuteButtonText => IsMuted ? "UNMUTE" : "MUTE";
    public string EnhancementStatusText => _enhancementOn ? "ENHANCEMENT ACTIVE" : "BYPASS (NORMAL AUDIO)";
    public string EnhancementStatusSubtext => _enhancementOn ? "DSP POWER ACTIVE" : "BYPASS (OFF)";

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string ProcessingText { get => _processingText; private set => SetProperty(ref _processingText, value); }

    public bool EnhancementOn
    {
        get => _enhancementOn;
        set
        {
            if (!SetProperty(ref _enhancementOn, value)) return;
            OnPropertyChanged(nameof(EnhancementLabel));
            OnPropertyChanged(nameof(EnhancementBtnLabel));
            OnPropertyChanged(nameof(EnhancementPowerButtonText));
            OnPropertyChanged(nameof(EnhancementPowerSubtext));
            OnPropertyChanged(nameof(EnhancementStatusText));
            OnPropertyChanged(nameof(EnhancementStatusSubtext));

            if (value)
            {
                _engine.UpdateSettings(CurrentSettings());
                if (_engine.State != EngineState.Running)
                {
                    _engine.Start();
                }
                _engine.Bypass = false;
            }
            else
            {
                _engine.Bypass = true;
                if (_engine.State == EngineState.Running || _engine.State == EngineState.Starting)
                {
                    _engine.Stop();
                }
            }

            UpdateFrequencyResponse();
            Refresh();
        }
    }

    public Preset SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (value is null || !SetProperty(ref _selectedPreset, value)) return;
            OnPropertyChanged(nameof(SelectedPresetName));
            OnPropertyChanged(nameof(ActivePresetHeader));
            if (value.IsCustom) return; // "Custom" keeps whatever the sliders currently show.

            bool preserveVolume = _engine is WasapiAudioEngine;
            ApplySliderValues(value.Settings, preserveMasterVolume: preserveVolume);
            PushSettings();
            UpdateFrequencyResponse();
        }
    }

    /// <summary>
    /// Part 4 Step 6. Crystal Clear is a separate one-tap mode.
    /// </summary>
    public bool CrystalClearOn
    {
        get => _crystalClearOn;
        set
        {
            if (!SetProperty(ref _crystalClearOn, value)) return;

            if (value)
            {
                _preCrystalClearSettings = CurrentSettings();
                ApplySliderValues(_presets.CrystalClear.Settings);
            }
            else
            {
                ApplySliderValues(_preCrystalClearSettings ?? _presets.Default.Settings);
                _preCrystalClearSettings = null;
            }

            SelectedPreset = _presets.Custom;
            PushSettings();
            UpdateFrequencyResponse();
        }
    }

    public AudioDeviceInfo? SelectedOutputDevice
    {
        get => _selectedOutputDevice;
        set
        {
            if (!SetProperty(ref _selectedOutputDevice, value)) return;
            _engine.SetOutputDevice(value?.Id);
            RefreshDevices();
        }
    }

    public bool IsStatusError => _engine.State == EngineState.Faulted;

    public string VirtualEndpointStatusText => _engine.GetVirtualEndpointStatus().Message;
    public bool IsVirtualEndpointInstalled => _engine.GetVirtualEndpointStatus().IsInstalled;

    public void UseVirtualOutputForCapture()
    {
        _engine.TryUseVirtualEndpointForCapture();
        Refresh();
    }

    private long _lastPresetAppliedTick;
    private bool _isMuted;

    public bool IsMuted => _isMuted || Volume.Value <= 0;
    public string GainDbReadout => IsMuted ? "MUTE" : $"+{SliderMap.VolumeDb((int)Volume.Value):0.0} dB";

    public void AdjustFader(SliderControl slider, double delta)
    {
        slider.Value = Math.Clamp(slider.Value + delta, 0, 100);
        SelectedPreset = _presets.Custom;
        PushSettings();
    }

    public void AdjustVolume(double delta)
    {
        _lastUserVolumeChangeTick = Environment.TickCount64;
        double target = Math.Clamp(Volume.Value + delta, 0, 100);
        Volume.Value = (int)Math.Round(target);
        SelectedPreset = _presets.Custom;
        PushSettings();
    }

    private EnhancerSettings CurrentSettings() => new(
        (int)Volume.Value, (int)Bass.Value, (int)Clarity.Value, (int)Voice.Value, (int)Detail.Value);

    private void PushSettings()
    {
        _engine.UpdateSettings(CurrentSettings());
        float targetScalar = (float)(Volume.Value / 100.0);
        _engine.SetMasterVolume(targetScalar);
        if (Volume.Value <= 0 && !_isMuted)
        {
            _isMuted = true;
            _engine.SetMasterMute(true);
            OnPropertyChanged(nameof(IsMuted));
            OnPropertyChanged(nameof(MuteButtonText));
        }
        else if (_isMuted && Volume.Value > 0)
        {
            _isMuted = false;
            _engine.SetMasterMute(false);
            OnPropertyChanged(nameof(IsMuted));
            OnPropertyChanged(nameof(MuteButtonText));
        }
        UpdateDialArc();
        UpdateFrequencyResponse();
        OnPropertyChanged(nameof(GainDbReadout));
        OnPropertyChanged(nameof(LoudnessText));
        OnPropertyChanged(nameof(LoudnessDbUnit));
    }

    private void ApplySliderValues(EnhancerSettings settings, bool preserveMasterVolume = false)
    {
        _lastPresetAppliedTick = Environment.TickCount64;
        _applyingPreset = true;
        try
        {
            if (!preserveMasterVolume)
            {
                Volume.Value = settings.Volume;
            }
            Bass.Value = settings.Bass;
            Clarity.Value = settings.Clarity;
            Voice.Value = settings.Voice;
            Detail.Value = settings.Detail;
            _engine.UpdateSettings(CurrentSettings());
            if (!preserveMasterVolume && _isMuted && settings.Volume > 0)
            {
                _isMuted = false;
                _engine.SetMasterMute(false);
                OnPropertyChanged(nameof(IsMuted));
                OnPropertyChanged(nameof(MuteButtonText));
            }
        }
        finally
        {
            _applyingPreset = false;
        }
        UpdateFrequencyResponse();
        OnPropertyChanged(nameof(GainDbReadout));
        OnPropertyChanged(nameof(LoudnessText));
        OnPropertyChanged(nameof(LoudnessDbUnit));
    }

    private void OnSliderChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applyingPreset || _syncingFromSystem) return;
        _lastUserVolumeChangeTick = Environment.TickCount64;
        SelectedPreset = _presets.Custom;
        PushSettings();
    }

    private void UpdateFrequencyResponse()
    {
        if (FrequencyBands.Count < 11 || _isUserAdjustingEq) return;

        if (IsMuted)
        {
            for (int i = 0; i < FrequencyBands.Count; i++)
            {
                FrequencyBands[i].Height = 12.0;
            }
            UpdateEqCurve();
            return;
        }

        if (!_enhancementOn)
        {
            // Bypass mode: flat response across the board
            for (int i = 0; i < FrequencyBands.Count; i++)
            {
                FrequencyBands[i].Height = 16.0;
            }
            UpdateEqCurve();
            return;
        }

        double bass = Bass.Value / 100.0;
        double voice = Voice.Value / 100.0;
        double clarity = Clarity.Value / 100.0;
        double detail = Detail.Value / 100.0;
        double baseline = 16.0;

        // Model frequency response shape corresponding to DSP filters
        double[] targetHeights = new double[]
        {
            baseline + (bass * 46.0),                       // 20 Hz
            baseline + (bass * 56.0),                       // 50 Hz
            baseline + (bass * 52.0),                       // 100 Hz
            baseline + (bass * 28.0 + voice * 8.0),         // 250 Hz
            baseline + (voice * 24.0),                      // 500 Hz
            baseline + (voice * 42.0),                      // 1 kHz
            baseline + (voice * 52.0 + clarity * 12.0),     // 2 kHz
            baseline + (voice * 26.0 + clarity * 38.0),     // 4 kHz
            baseline + (clarity * 46.0 + detail * 22.0),    // 8 kHz
            baseline + (clarity * 52.0 + detail * 38.0),    // 16 kHz
            baseline + (clarity * 38.0 + detail * 46.0),    // 20 kHz
        };

        for (int i = 0; i < targetHeights.Length; i++)
        {
            FrequencyBands[i].Height = Math.Clamp(targetHeights[i], 12.0, 96.0);
        }
        UpdateEqCurve();
    }

    private void OnVisualizerTick(object? sender, EventArgs e)
    {
        _visualizerTick++;

        // 1. Sample real-time FFT spectrum & energy analysis from the render stream
        try
        {
            if (_engine.State == EngineState.Running)
            {
                _engine.GetSpectrumAnalysis(_spectrumBands, out float peak, out float rms, out bool active);
                PeakDb = peak;
                RmsDb = rms;
                IsAudioActive = active;
            }
            else
            {
                Array.Clear(_spectrumBands, 0, _spectrumBands.Length);
                PeakDb = -120f;
                RmsDb = -120f;
                IsAudioActive = false;
            }

            OnPropertyChanged(nameof(SpectrumBands));
            OnPropertyChanged(nameof(PeakDbText));
            OnPropertyChanged(nameof(RmsDbText));
        }
        catch
        {
            // Defensive error isolation: analyzer sampling must never crash UI
        }

        // 2. Hardware Volume & Mute (keyboard Fn keys, taskbar slider) synchronization
        long now = Environment.TickCount64;
        if (_visualizerTick % 2 == 0 && (now - _lastUserVolumeChangeTick) > 1000)
        {
            float? liveVol = _engine.GetMasterVolume();
            if (liveVol.HasValue)
            {
                int intVol = (int)Math.Clamp(Math.Round(liveVol.Value * 100.0), 0, 100);
                if (Math.Abs((int)Volume.Value - intVol) >= 1)
                {
                    _syncingFromSystem = true;
                    try
                    {
                        Volume.Value = intVol;
                        // Synchronize physical output volume with laptop keyboard Fn keys / Windows slider
                        _engine.SetMasterVolume(liveVol.Value);
                        _engine.UpdateSettings(CurrentSettings());
                        UpdateDialArc();
                        OnPropertyChanged(nameof(GainDbReadout));
                        OnPropertyChanged(nameof(LoudnessText));
                        OnPropertyChanged(nameof(LoudnessDbUnit));
                    }
                    finally
                    {
                        _syncingFromSystem = false;
                    }
                }
            }

            bool? liveMute = _engine.GetMasterMute();
            if (liveMute.HasValue && liveMute.Value != _isMuted)
            {
                _isMuted = liveMute.Value;
                _engine.SetMasterMute(_isMuted);
                UpdateFrequencyResponse();
                OnPropertyChanged(nameof(IsMuted));
                OnPropertyChanged(nameof(MuteButtonText));
                OnPropertyChanged(nameof(GainDbReadout));
                OnPropertyChanged(nameof(LoudnessText));
                OnPropertyChanged(nameof(LoudnessDbUnit));
            }
        }

        UpdateSpectrumVisualization();
    }

    private void OnEqBandAdjusted(int bandIndex, double gainDb)
    {
        if (_applyingPreset) return;
        _isUserAdjustingEq = true;
        try
        {
            int mappedVal = (int)Math.Clamp(gainDb > 0 ? (gainDb / 12.0) * 100.0 : 0, 0, 100);
            switch (bandIndex)
            {
                case 0 or 1 or 2:
                    Bass.Value = mappedVal;
                    break;
                case 3 or 4 or 5:
                    Voice.Value = mappedVal;
                    break;
                case 6 or 7 or 8:
                    Clarity.Value = mappedVal;
                    break;
                case 9 or 10:
                    Detail.Value = mappedVal;
                    break;
            }
            SelectedPreset = _presets.Custom;
            PushSettings();
            UpdateEqCurve();
        }
        finally
        {
            _isUserAdjustingEq = false;
        }
    }

    private void UpdateDialArc()
    {
        double t = Math.Clamp(Volume.Value / 100.0, 0.0, 1.0);
        KnobRotationAngle = -135.0 + t * 270.0;

        double startAngleRad = 135.0 * Math.PI / 180.0;
        double startX = 80.0 + 66.0 * Math.Cos(startAngleRad);
        double startY = 80.0 + 66.0 * Math.Sin(startAngleRad);

        double currentAngleDeg = 135.0 + t * 270.0;
        double currentAngleRad = currentAngleDeg * Math.PI / 180.0;
        double endX = 80.0 + 66.0 * Math.Cos(currentAngleRad);
        double endY = 80.0 + 66.0 * Math.Sin(currentAngleRad);

        bool isLargeArc = (currentAngleDeg - 135.0) > 180.0;

        if (t <= 0.005)
        {
            DialArcPathData = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"M {startX:0.#},{startY:0.#} A 66,66 0 0 1 {startX + 0.1:0.#},{startY + 0.1:0.#}");
        }
        else
        {
            DialArcPathData = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"M {startX:0.#},{startY:0.#} A 66,66 0 {(isLargeArc ? 1 : 0)} 1 {endX:0.#},{endY:0.#}");
        }
    }

    private void UpdateEqCurve()
    {
        if (FrequencyBands == null || FrequencyBands.Count == 0) return;

        var sb = new System.Text.StringBuilder(128);
        double startY = 20.0 - (FrequencyBands[0].GainDb * 1.2);
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"M 0,{startY:0.#}");

        for (int i = 0; i < FrequencyBands.Count; i++)
        {
            double x = (i / (double)(FrequencyBands.Count - 1)) * 100.0;
            double y = 20.0 - (FrequencyBands[i].GainDb * 1.2);
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $" L {x:0.#},{y:0.#}");
        }
        EqCurvePathData = sb.ToString();
    }

    private void UpdateSpectrumVisualization()
    {
        const int numPoints = 16;
        const double width = 800.0;
        var points = new (double X, double Y)[numPoints + 1];

        bool active = _isAudioActive && _enhancementOn && _engine.State == EngineState.Running;

        for (int i = 0; i <= numPoints; i++)
        {
            double x = (i / (double)numPoints) * width;
            double y = 154.0;

            if (active)
            {
                int bandIdx = Math.Clamp((int)Math.Round((i / (double)numPoints) * 31.0), 0, 31);
                float mag = _spectrumBands[bandIdx];
                if (bandIdx > 0) mag = Math.Max(mag, _spectrumBands[bandIdx - 1] * 0.85f);
                if (bandIdx < 31) mag = Math.Max(mag, _spectrumBands[bandIdx + 1] * 0.85f);

                y = Math.Clamp(154.0 - (mag * 135.0), 16.0, 154.0);
            }
            else if (_enhancementOn && _engine.State == EngineState.Running)
            {
                y = 152.0 + Math.Sin(_visualizerTick * 0.15 + i * 0.4) * 2.0;
            }

            points[i] = (x, y);
        }

        var sbLine = new System.Text.StringBuilder(256);
        sbLine.Append(System.Globalization.CultureInfo.InvariantCulture, $"M 0,{points[0].Y:0.#}");
        for (int i = 0; i < points.Length - 1; i++)
        {
            double mx = (points[i].X + points[i + 1].X) / 2.0;
            double my = (points[i].Y + points[i + 1].Y) / 2.0;
            sbLine.Append(System.Globalization.CultureInfo.InvariantCulture, $" Q {points[i].X:0.#},{points[i].Y:0.#} {mx:0.#},{my:0.#}");
        }
        sbLine.Append(System.Globalization.CultureInfo.InvariantCulture, $" T {width:0.#},{points[^1].Y:0.#}");

        string lineData = sbLine.ToString();
        SpectrumLinePathData = lineData;
        SpectrumFillPathData = $"{lineData} L 800,160 L 0,160 Z";

        var sbBars = new System.Text.StringBuilder(512);
        for (int i = 0; i < 32; i++)
        {
            double barX = i * 25.0 + 4.0;
            double barW = 14.0;
            double h = 2.0;

            if (active)
            {
                h = Math.Max(2.0, _spectrumBands[i] * 140.0);
            }
            else if (_enhancementOn && _engine.State == EngineState.Running)
            {
                h = Math.Max(2.0, 2.0 + Math.Sin(_visualizerTick * 0.2 + i * 0.5) * 2.0);
            }

            double barY = 156.0 - h;
            sbBars.Append(System.Globalization.CultureInfo.InvariantCulture, $"M {barX:0.#},{barY:0.#} h {barW} v {h:0.#} h -{barW} Z ");
        }
        SpectrumBarsPathData = sbBars.ToString();

        UpdateDialArc();
        UpdateEqCurve();
        OnPropertyChanged(nameof(DynamicHeadroomText));
        OnPropertyChanged(nameof(LoudnessText));
    }

    private void OnEngineStateChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Refresh();
            if (_enhancementOn && _engine.State == EngineState.Faulted)
            {
                // Auto-recover cleanly if a momentary device transition occurred during a YouTube ad/video switch
                Task.Delay(150).ContinueWith(_ =>
                {
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        if (_enhancementOn && _engine.State != EngineState.Running)
                        {
                            _engine.Start();
                            _engine.Bypass = false;
                            Refresh();
                        }
                    });
                });
            }
        });
    }

    private void OnDefaultOutputChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(RefreshDevices);

    private void OnDevicesChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(RefreshDevices);

    public void RefreshDevicesForTests() => RefreshDevices();

    private void RefreshDevices()
    {
        var physicalDevices = OutputDevices;
        var defaultOut = _devices.GetDefaultOutput();

        if (_selectedOutputDevice is not null && physicalDevices.Any(d => d.Id == _selectedOutputDevice.Id))
        {
            OutputDeviceName = _selectedOutputDevice.Name;
        }
        else if (!VirtualEndpointDetector.IsVirtualEndpoint(defaultOut))
        {
            OutputDeviceName = defaultOut.Name;
        }
        else
        {
            // Windows default is currently the Virtual Cable, so display the active physical target
            var headphoneDev = physicalDevices.FirstOrDefault(d => d.IsHeadphones);
            OutputDeviceName = headphoneDev?.Name ?? physicalDevices.FirstOrDefault()?.Name ?? defaultOut.Name;
        }

        OnPropertyChanged(nameof(OutputDevices));
        OnPropertyChanged(nameof(IsHeadphonesConnected));
        OnPropertyChanged(nameof(HeadphonesBadgeVisibility));
        OnPropertyChanged(nameof(SpeakersIconVisibility));
        OnPropertyChanged(nameof(HeadphonesIconVisibility));
        OnPropertyChanged(nameof(VirtualEndpointStatusText));
        OnPropertyChanged(nameof(IsVirtualEndpointInstalled));

        if (_selectedOutputDevice is not null &&
            !OutputDevices.Any(d => d.Id == _selectedOutputDevice.Id))
        {
            SelectedOutputDevice = null;
        }
    }

    private void Refresh()
    {
        StatusText = _engine.StatusMessage;
        ProcessingText = _engine.State == EngineState.Running
            ? (_enhancementOn ? "Enhancing audio" : "Bypass (normal audio)")
            : _enhancementOn ? "Starting..." : "Idle";
        OnPropertyChanged(nameof(IsStatusError));
        OnPropertyChanged(nameof(VirtualEndpointStatusText));
        OnPropertyChanged(nameof(IsVirtualEndpointInstalled));
        OnPropertyChanged(nameof(EnhancementStatusText));
    }

    public void Dispose()
    {
        _visualizerTimer?.Stop();
        if (_enhancementOn)
        {
            _enhancementOn = false;
            try { _engine.Stop(); } catch { }
        }
        _engine.StateChanged -= OnEngineStateChanged;
        _devices.DefaultOutputChanged -= OnDefaultOutputChanged;
        _devices.DevicesChanged -= OnDevicesChanged;
        try { WindowsDefaultAudioEndpointRouter.EnsurePhysicalDefaultRestored(forceSpeakers: false); } catch { }
    }
}
