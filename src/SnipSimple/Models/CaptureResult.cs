using System.Windows;
using System.Windows.Media.Imaging;

namespace SnipSimple.Models;

public class CaptureResult
{
    public required BitmapSource Image { get; init; }
    public Rect Bounds { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
}
