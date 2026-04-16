using System.Runtime.InteropServices;
using System.Windows;

namespace SnipSimple.Helpers;

public class WindowInfo
{
    public IntPtr Handle { get; init; }
    public string Title { get; init; } = string.Empty;
    public Rect Bounds { get; init; }
}

public static class WindowEnumerator
{
    public static List<WindowInfo> GetVisibleWindows()
    {
        var windows = new List<WindowInfo>();
        var shellWindow = NativeMethods.GetShellWindow();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (hWnd == shellWindow) return true;
            if (!NativeMethods.IsWindowVisible(hWnd)) return true;
            if (NativeMethods.IsIconic(hWnd)) return true;

            var exStyle = NativeMethods.GetWindowLongW(hWnd, NativeMethods.GWL_EXSTYLE);
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0 &&
                (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0)
                return true;

            var titleLength = NativeMethods.GetWindowTextLengthW(hWnd);
            if (titleLength == 0) return true;

            var titleChars = new char[titleLength + 1];
            NativeMethods.GetWindowTextW(hWnd, titleChars, titleChars.Length);
            var title = new string(titleChars, 0, titleLength);

            var bounds = GetWindowBounds(hWnd);
            if (bounds.Width <= 0 || bounds.Height <= 0) return true;

            windows.Add(new WindowInfo
            {
                Handle = hWnd,
                Title = title,
                Bounds = bounds
            });

            return true;
        }, IntPtr.Zero);

        return windows;
    }

    public static Rect GetWindowBounds(IntPtr hWnd)
    {
        // Try DWM extended frame bounds first (accounts for shadows)
        var hr = NativeMethods.DwmGetWindowAttribute(
            hWnd,
            NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
            out NativeMethods.RECT dwmRect,
            Marshal.SizeOf<NativeMethods.RECT>());

        if (hr == 0)
        {
            return new Rect(dwmRect.Left, dwmRect.Top, dwmRect.Width, dwmRect.Height);
        }

        // Fallback to GetWindowRect
        NativeMethods.GetWindowRect(hWnd, out NativeMethods.RECT rect);
        return new Rect(rect.Left, rect.Top, rect.Width, rect.Height);
    }

    public static IntPtr GetWindowAtPoint(System.Drawing.Point screenPoint)
    {
        var pt = new NativeMethods.POINT { X = screenPoint.X, Y = screenPoint.Y };
        var hWnd = NativeMethods.WindowFromPoint(pt);
        if (hWnd == IntPtr.Zero) return IntPtr.Zero;

        // Get the root owner window
        return NativeMethods.GetAncestor(hWnd, NativeMethods.GA_ROOTOWNER);
    }
}
