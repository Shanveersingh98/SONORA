using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AudioEnhancer.UI;

/// <summary>
/// Code-behind is intentionally almost empty: all behavior is in MainViewModel.
/// The only thing here is asking Windows 10/11 for a dark title bar so the
/// window frame matches the dark UI.
/// </summary>
public partial class MainWindow : Window
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public MainWindow() => InitializeComponent();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            int on = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        }
        catch { /* purely cosmetic - ignore on systems that don't support it */ }
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // Ensure system media and volume keys (Mute, Volume Up/Down, Play/Pause) pass directly to Windows Shell
        if (e.Key is System.Windows.Input.Key.VolumeMute
                 or System.Windows.Input.Key.VolumeDown
                 or System.Windows.Input.Key.VolumeUp
                 or System.Windows.Input.Key.MediaPlayPause
                 or System.Windows.Input.Key.MediaNextTrack
                 or System.Windows.Input.Key.MediaPreviousTrack
                 or System.Windows.Input.Key.MediaStop)
        {
            e.Handled = false;
        }
    }

    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
        }
        else
        {
            try { DragMove(); } catch { }
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            try { vm.EnhancementOn = false; } catch { }
            try { vm.Dispose(); } catch { }
        }
        try { AudioEnhancer.Audio.WindowsDefaultAudioEndpointRouter.EnsurePhysicalDefaultRestored(forceSpeakers: false); } catch { }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        try { AudioEnhancer.Audio.WindowsDefaultAudioEndpointRouter.EnsurePhysicalDefaultRestored(forceSpeakers: false); } catch { }
        base.OnClosed(e);
        // Force immediate clean exit so no zombie threads, keep-alive loops, or COM listeners linger
        Environment.Exit(0);
    }

    private void Knob_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            int delta = e.Delta > 0 ? 3 : -3;
            vm.AdjustVolume(delta);
        }
    }

    private void Knob_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            if (vm.Volume.Value >= 95)
                vm.AdjustVolume(-75);
            else
                vm.AdjustVolume(10);
        }
    }

    private void Knob_MouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.AdjustVolume(-10);
        }
    }
}
