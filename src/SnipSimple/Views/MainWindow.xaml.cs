using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SnipSimple.Models;
using SnipSimple.Services;

namespace SnipSimple.Views;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly HotkeyService _hotkeyService;
    private readonly List<EditorWindow> _openEditors = new();
    private AppTheme _currentTheme = AppTheme.Light;

    public MainWindow()
    {
        InitializeComponent();

        _settingsService = new SettingsService();
        _settingsService.Load();

        // Apply saved theme (defaults to Light)
        _currentTheme = _settingsService.Settings.MainWindowTheme == "Dark"
            ? AppTheme.Dark : AppTheme.Light;
        ThemeService.ApplyThemeToWindow(this, _currentTheme);
        UpdateThemeToggleIcon();

        _hotkeyService = new HotkeyService();
        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        _hotkeyService.Install();

        Closed += (_, _) => _hotkeyService.Dispose();
    }

    #region Theme Toggle

    private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        _currentTheme = ThemeService.ToggleTheme(this, _currentTheme);
        _settingsService.Settings.MainWindowTheme = _currentTheme.ToString();
        _settingsService.Save();
        UpdateThemeToggleIcon();
    }

    private void UpdateThemeToggleIcon()
    {
        BtnThemeToggle.Content = _currentTheme == AppTheme.Dark ? "\u263E" : "\u2600";
        BtnThemeToggle.ToolTip = _currentTheme == AppTheme.Dark
            ? "Switch to light theme"
            : "Switch to dark theme";
    }

    #endregion

    private int GetDelaySeconds()
    {
        if (CmbDelay.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            return int.Parse(tag);
        return 0;
    }

    private void BtnRegion_Click(object sender, RoutedEventArgs e) => StartSnip(SnipMode.Region);
    private void BtnFullScreen_Click(object sender, RoutedEventArgs e) => StartSnip(SnipMode.FullScreen);
    private void BtnWindow_Click(object sender, RoutedEventArgs e) => StartSnip(SnipMode.Window);

    private void OnHotkeyPressed()
    {
        Dispatcher.Invoke(() => StartSnip(SnipMode.Region));
    }

    private async void StartSnip(SnipMode mode)
    {
        int delay = GetDelaySeconds();
        bool forgetLast = ChkForgetLast.IsChecked == true;

        // If "forget last snip" is checked, close prior editor(s)
        if (forgetLast)
        {
            foreach (var editor in _openEditors.ToList())
            {
                editor.Close();
            }
            _openEditors.Clear();
        }

        if (delay > 0)
        {
            WindowState = WindowState.Minimized;
            HideAllEditors();
            await ShowCountdown(delay);
        }
        else
        {
            HideAllEditors();
        }

        // Hide main window during capture
        Hide();

        // Small delay to let the windows fully hide
        await Task.Delay(200);

        CaptureResult? result = null;

        // Get DPI scale for fullscreen capture
        double dpiScaleX = 1.0, dpiScaleY = 1.0;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
            dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
        }

        switch (mode)
        {
            case SnipMode.FullScreen:
                result = ScreenCaptureService.CaptureFullScreen(dpiScaleX, dpiScaleY);
                break;

            case SnipMode.Region:
                result = await ShowRegionSelector();
                break;

            case SnipMode.Window:
                result = await ShowWindowSelector();
                break;
        }

        EditorWindow? newEditor = null;
        if (result != null)
        {
            ClipboardService.CopyToClipboard(result.Image);
            newEditor = new EditorWindow(result, _settingsService);
            _openEditors.Add(newEditor);
            newEditor.Closed += (_, _) => _openEditors.Remove(newEditor);
        }

        // Show older editors first (they go behind)
        ShowAllEditors();

        // Show main window
        Show();
        WindowState = WindowState.Normal;

        // Show new editor LAST so it's on top of everything
        if (newEditor != null)
        {
            newEditor.Show();
            newEditor.Activate();
        }
    }

    private void HideAllEditors()
    {
        foreach (var editor in _openEditors)
        {
            editor.Hide();
        }
    }

    private void ShowAllEditors()
    {
        foreach (var editor in _openEditors)
        {
            if (editor.IsVisible) continue; // Skip if already shown (e.g., new editor not yet shown)
            editor.Show();
        }
    }

    private async Task ShowCountdown(int seconds)
    {
        var countdown = new CountdownWindow(seconds);
        countdown.Show();

        while (seconds > 0)
        {
            await Task.Delay(1000);
            seconds--;
            countdown.UpdateCount(seconds);
        }

        countdown.Close();
    }

    private Task<CaptureResult?> ShowRegionSelector()
    {
        var tcs = new TaskCompletionSource<CaptureResult?>();
        var overlay = new OverlayWindow(SnipMode.Region);
        overlay.CaptureCompleted += result =>
        {
            tcs.TrySetResult(result);
        };
        overlay.CaptureCancelled += () =>
        {
            tcs.TrySetResult(null);
        };
        overlay.Show();
        return tcs.Task;
    }

    private Task<CaptureResult?> ShowWindowSelector()
    {
        var tcs = new TaskCompletionSource<CaptureResult?>();
        var overlay = new OverlayWindow(SnipMode.Window);
        overlay.CaptureCompleted += result =>
        {
            tcs.TrySetResult(result);
        };
        overlay.CaptureCancelled += () =>
        {
            tcs.TrySetResult(null);
        };
        overlay.Show();
        return tcs.Task;
    }
}
