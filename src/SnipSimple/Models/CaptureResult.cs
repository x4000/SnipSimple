using System.Windows;
using System.Windows.Media.Imaging;

namespace SnipSimple.Models;

public class CaptureResult
{
    public required BitmapSource Image { get; init; }
    public Rect Bounds { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>
    /// The DPI scale factor of the screen where the capture was taken.
    /// Used to display the image at 1:1 pixel mapping in the editor.
    /// </summary>
    public double DpiScaleX { get; init; } = 1.0;
    public double DpiScaleY { get; init; } = 1.0;
}
