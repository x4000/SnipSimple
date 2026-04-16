using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnipSimple.Helpers;

public static class BitmapHelper
{
    public static BitmapSource? HBitmapToBitmapSource(IntPtr hBitmap)
    {
        try
        {
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            NativeMethods.DeleteObject(hBitmap);
        }
    }

    public static RenderTargetBitmap RenderVisualToBitmap(Visual visual, int width, int height, double dpiX = 96, double dpiY = 96)
    {
        var rtb = new RenderTargetBitmap(width, height, dpiX, dpiY, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    public static BitmapEncoder GetEncoderForExtension(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 95 },
            ".bmp" => new BmpBitmapEncoder(),
            _ => new PngBitmapEncoder()
        };
    }

    public static void SaveBitmapToFile(BitmapSource bitmap, string filePath)
    {
        var extension = System.IO.Path.GetExtension(filePath);
        var encoder = GetEncoderForExtension(extension);
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = System.IO.File.Create(filePath);
        encoder.Save(stream);
    }

    public static BitmapSource FlattenWithAnnotations(BitmapSource background, Visual annotationVisual, int width, int height)
    {
        var drawingVisual = new DrawingVisual();
        using (var dc = drawingVisual.RenderOpen())
        {
            dc.DrawImage(background, new Rect(0, 0, width, height));
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(drawingVisual);
        rtb.Render(annotationVisual);
        rtb.Freeze();
        return rtb;
    }
}
