using System.IO;
using System.Threading;
using System.Windows;
using AudioEnhancer.Audio;
using AudioEnhancer.Presets;
using AudioEnhancer.UI;

namespace AudioEnhancer;

/// <summary>
/// Composition root: the ONE place that decides which concrete classes are used.
/// </summary>
public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private IAudioBackend? _backend;
    private IAudioEngine? _engine;
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        const string mutexName = "Global\\SonoraAudioEnhancerSingleInstanceMutex";
        _singleInstanceMutex = new Mutex(true, mutexName, out bool isNewInstance);
        if (!isNewInstance)
        {
            // Another instance of Sonora is already active. Exit immediately to prevent collisions.
            Environment.Exit(0);
            return;
        }

        base.OnStartup(e);

        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_run.log");
        void Log(string msg)
        {
            try { File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n"); } catch { }
        }

        Log("App OnStartup started.");

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            Log($"[CRASH] Unhandled: {args.ExceptionObject}");
        };

        DispatcherUnhandledException += (_, args) =>
        {
            Log($"[CRASH] DispatcherUnhandled: {args.Exception}");
            args.Handled = true;
        };

        try
        {
            // 0. Ensure virtual endpoints are visible on startup so they are discovered
            try
            {
                WindowsDefaultAudioEndpointRouter.EnsureAllVirtualEndpointsVisible();
            }
            catch { }

            Log("Creating AudioBackend...");
            _backend = AudioBackend.CreateForCurrentPlatform();
            Log($"AudioBackend created. IsAvailable={_backend.IsAvailable}");

            _engine = _backend.IsAvailable
                ? (IAudioEngine)new WasapiAudioEngine(_backend)
                : new NullAudioEngine();
            Log($"AudioEngine created: {_engine.GetType().Name}, State={_engine.State}");

            // 1. If previous session crashed with virtual endpoint as default, recover gracefully
            try
            {
                var defaultOut = _backend.Devices.GetDefaultOutput();
                if (defaultOut != null && VirtualEndpointDetector.IsVirtualEndpoint(defaultOut))
                {
                    WindowsDefaultAudioEndpointRouter.EnsurePhysicalDefaultRestored(forceSpeakers: false);
                }
            }
            catch { }

            Log("Creating MainViewModel...");
            _viewModel = new MainViewModel(_engine, _backend.Devices, new BuiltInPresetManager());
            Log("MainViewModel created successfully.");

            // 2. Start with Enhancement active so all presets, dials, and controls respond immediately
            _viewModel.EnhancementOn = true;
            Log("App initialized with Enhancement ON.");

            // Safeguard: Ensure Windows default playback endpoint is restored when process exits
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { WindowsDefaultAudioEndpointRouter.EnsurePhysicalDefaultRestored(forceSpeakers: false); } catch { }
                try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
            };

            Log("Creating MainWindow...");
            var window = new MainWindow { DataContext = _viewModel };
            MainWindow = window;
            Log("MainWindow created. Showing window...");
            window.Show();
            Log("window.Show() called.");
        }
        catch (Exception ex)
        {
            Log($"[CRASH] Startup Exception: {ex}");
            MessageBox.Show(ex.ToString(), "Sonora Audio Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _viewModel?.Dispose(); } catch { }
        try { _engine?.Dispose(); } catch { }
        try { _backend?.Dispose(); } catch { }
        try { WindowsDefaultAudioEndpointRouter.EnsurePhysicalDefaultRestored(forceSpeakers: false); } catch { }
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
