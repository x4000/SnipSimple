using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SnipSimple.Helpers;

namespace SnipSimple.Services;

public enum AppTheme
{
    Light,
    Dark
}

public static class ThemeService
{
    private static readonly Uri LightThemeUri = new("Resources/ThemeLight.xaml", UriKind.Relative);
    private static readonly Uri DarkThemeUri = new("Resources/ThemeDark.xaml", UriKind.Relative);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>
    /// Apply a theme to a specific window's resources and title bar.
    /// </summary>
    public static void ApplyThemeToWindow(Window window, AppTheme theme)
    {
        var mergedDicts = window.Resources.MergedDictionaries;

        // Remove any existing theme dictionary from this window
        ResourceDictionary? existing = null;
        foreach (var dict in mergedDicts)
        {
            if (dict.Source != null &&
                (dict.Source.OriginalString.Contains("ThemeLight") ||
                 dict.Source.OriginalString.Contains("ThemeDark")))
            {
                existing = dict;
                break;
            }
        }
        if (existing != null)
            mergedDicts.Remove(existing);

        // Add new theme resources
        var uri = theme == AppTheme.Dark ? DarkThemeUri : LightThemeUri;
        mergedDicts.Add(new ResourceDictionary { Source = uri });

        // Apply dark/light title bar
        if (window.IsLoaded)
        {
            SetDarkTitleBar(window, theme == AppTheme.Dark);
        }
        else
        {
            window.SourceInitialized += (_, _) => SetDarkTitleBar(window, theme == AppTheme.Dark);
        }
    }

    /// <summary>
    /// Toggle a window's theme and return the new theme.
    /// </summary>
    public static AppTheme ToggleTheme(Window window, AppTheme currentTheme)
    {
        var newTheme = currentTheme == AppTheme.Light ? AppTheme.Dark : AppTheme.Light;
        ApplyThemeToWindow(window, newTheme);
        return newTheme;
    }

    private static void SetDarkTitleBar(Window window, bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int value = dark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch
        {
            // Silently fail on older Windows versions
        }
    }
}
