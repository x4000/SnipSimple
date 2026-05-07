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

    // Solo-mode state: once we enter solo mode the MainWindow stays hidden
    // until app exit and the single editor handles all subsequent snips.
    private bool _inSoloMode;
    private EditorWindow? _soloEditor;

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

        // Apply saved solo-window preference
        ChkSoloWindow.IsChecked = _settingsService.Settings.SoloWindow;

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
        BtnThemeToggle.Content = _currentTheme == AppTheme.Dark ? "☾" : "☀";
        BtnThemeToggle.ToolTip = _currentTheme == AppTheme.Dark
            ? "Switch to light theme"
            : "Switch to dark theme";
    }

    #endregion

    private void ChkSoloWindow_Click(object sender, RoutedEventArgs e)
    {
        _settingsService.Settings.SoloWindow = ChkSoloWindow.IsChecked == true;
        _settingsService.Save();
    }

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
        Dispatcher.Invoke(() =>
        {
            if (_inSoloMode && _soloEditor != null)
            {
                _ = _soloEditor.RequestSnipFromHotkey(SnipMode.Region);
            }
            else
            {
                StartSnip(SnipMode.Region);
            }
        });
    }

    /// <summary>
    /// Performs the screen capture flow (delay, overlay, capture). Used by
    /// both MainWindow's Start path and the solo EditorWindow's snip path.
    /// Caller is responsible for hiding/showing whatever UI shouldn't be
    /// visible during capture.
    /// </summary>
    public async Task<CaptureResult?> PerformSnipAsync(SnipMode mode, int delaySeconds)
    {
        if (delaySeconds > 0)
        {
            await ShowCountdown(delaySeconds);
        }

        // Small delay to let any caller-hidden windows fully disappear
        await Task.Delay(200);

        double dpiScaleX = 1.0, dpiScaleY = 1.0;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
            dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
        }

        return mode switch
        {
            SnipMode.FullScreen => ScreenCaptureService.CaptureFullScreen(dpiScaleX, dpiScaleY),
            SnipMode.Region => await ShowRegionSelector(dpiScaleX, dpiScaleY),
            SnipMode.Window => await ShowWindowSelector(),
            _ => null
        };
    }

    private async void StartSnip(SnipMode mode)
    {
        int delay = GetDelaySeconds();
        bool forgetLast = ChkForgetLast.IsChecked == true;
        bool soloMode = ChkSoloWindow.IsChecked == true;

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
        }
        HideAllEditors();
        Hide();

        var result = await PerformSnipAsync(mode, delay);

        EditorWindow? newEditor = null;
        if (result != null)
        {
            ClipboardService.CopyToClipboard(result.Image);

            if (soloMode)
            {
                // Enter solo mode: keep MainWindow hidden permanently and
                // hand off to a combined snip+edit editor window.
                _inSoloMode = true;
                _soloEditor = new EditorWindow(result, _settingsService,
                    soloOwner: this, initialDelay: delay);
                _soloEditor.Closed += (_, _) =>
                {
                    _soloEditor = null;
                    Application.Current.Shutdown();
                };
                _soloEditor.Show();
                _soloEditor.Activate();
                return;
            }

            newEditor = new EditorWindow(result, _settingsService);
            _openEditors.Add(newEditor);
            newEditor.Closed += (_, _) => _openEditors.Remove(newEditor);
        }

        // Non-solo path: restore older editors and main window, then surface
        // the new editor on top.
        ShowAllEditors();
        Show();
        WindowState = WindowState.Normal;

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

    private Task<CaptureResult?> ShowRegionSelector(double dpiScaleX, double dpiScaleY)
    {
        // Snapshot all screens BEFORE the overlay appears. Showing the overlay
        // activates it and dismisses any open context menus, so we need to
        // capture first and let the overlay render the frozen image instead
        // of the live desktop. The user's drag then crops from the snapshot.
        var snapshot = ScreenCaptureService.CaptureFullScreen(dpiScaleX, dpiScaleY);

        var tcs = new TaskCompletionSource<CaptureResult?>();
        var overlay = new OverlayWindow(SnipMode.Region, snapshot?.Image);
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
