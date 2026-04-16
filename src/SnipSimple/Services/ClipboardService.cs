using System.Windows;
using System.Windows.Media.Imaging;

namespace SnipSimple.Services;

public static class ClipboardService
{
    public static void CopyToClipboard(BitmapSource bitmap)
    {
        Clipboard.SetImage(bitmap);
    }
}
