using System.Windows;
using System.Windows.Media.Imaging;
using SnipSimple.Helpers;
using SnipSimple.Models;

namespace SnipSimple.Services;

public static class ScreenCaptureService
{
    public static CaptureResult? CaptureRegion(Rect bounds)
    {
        int x = (int)bounds.X;
        int y = (int)bounds.Y;
        int width = (int)bounds.Width;
        int height = (int)bounds.Height;

        if (width <= 0 || height <= 0) return null;

        var bitmap = CaptureScreenArea(x, y, width, height);
        if (bitmap == null) return null;

        return new CaptureResult
        {
            Image = bitmap,
            Bounds = bounds
        };
    }

    public static CaptureResult? CaptureFullScreen()
    {
        var virtualScreen = GetVirtualScreenBounds();
        return CaptureRegion(virtualScreen);
    }

    public static CaptureResult? CaptureWindow(IntPtr hWnd)
    {
        var bounds = WindowEnumerator.GetWindowBounds(hWnd);
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;

        int width = (int)bounds.Width;
        int height = (int)bounds.Height;

        // Try PrintWindow first for better results with off-screen windows
        var hdcScreen = NativeMethods.GetDC(IntPtr.Zero);
        var hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
        var hBitmap = NativeMethods.CreateCompatibleBitmap(hdcScreen, width, height);
        var hOld = NativeMethods.SelectObject(hdcMem, hBitmap);

        bool success = NativeMethods.PrintWindow(hWnd, hdcMem, NativeMethods.PW_RENDERFULLCONTENT);

        NativeMethods.SelectObject(hdcMem, hOld);
        NativeMethods.DeleteDC(hdcMem);
        NativeMethods.ReleaseDC(IntPtr.Zero, hdcScreen);

        if (!success)
        {
            NativeMethods.DeleteObject(hBitmap);
            // Fallback to region capture
            return CaptureRegion(bounds);
        }

        var bitmap = BitmapHelper.HBitmapToBitmapSource(hBitmap);
        if (bitmap == null) return null;

        return new CaptureResult
        {
            Image = bitmap,
            Bounds = bounds
        };
    }

    private static BitmapSource? CaptureScreenArea(int x, int y, int width, int height)
    {
        var hdcScreen = NativeMethods.GetDC(IntPtr.Zero);
        var hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
        var hBitmap = NativeMethods.CreateCompatibleBitmap(hdcScreen, width, height);
        var hOld = NativeMethods.SelectObject(hdcMem, hBitmap);

        NativeMethods.BitBlt(hdcMem, 0, 0, width, height, hdcScreen, x, y,
            NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);

        NativeMethods.SelectObject(hdcMem, hOld);
        NativeMethods.DeleteDC(hdcMem);
        NativeMethods.ReleaseDC(IntPtr.Zero, hdcScreen);

        return BitmapHelper.HBitmapToBitmapSource(hBitmap);
    }

    public static Rect GetVirtualScreenBounds()
    {
        double left = SystemParameters.VirtualScreenLeft;
        double top = SystemParameters.VirtualScreenTop;
        double width = SystemParameters.VirtualScreenWidth;
        double height = SystemParameters.VirtualScreenHeight;
        return new Rect(left, top, width, height);
    }

    public static Rect GetPrimaryScreenBounds()
    {
        return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
    }
}
