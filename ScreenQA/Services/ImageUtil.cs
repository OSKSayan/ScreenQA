using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using DrawingImaging = System.Drawing.Imaging;

namespace ScreenQA.Services;

internal static class ImageUtil
{
    public static BitmapSource ToBitmapSource(Drawing.Bitmap bitmap)
    {
        var rect = new Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, DrawingImaging.ImageLockMode.ReadOnly, DrawingImaging.PixelFormat.Format32bppArgb);
        try
        {
            var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgr32, null,
                data.Scan0, data.Stride * bitmap.Height, data.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    public static byte[] ToPng(Drawing.Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, DrawingImaging.ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>Масштабирует изображение; при scale == 1 возвращает копию.</summary>
    public static Drawing.Bitmap Scale(Drawing.Bitmap source, double scale)
    {
        int w = Math.Max(1, (int)Math.Round(source.Width * scale));
        int h = Math.Max(1, (int)Math.Round(source.Height * scale));
        var result = new Drawing.Bitmap(w, h, DrawingImaging.PixelFormat.Format32bppArgb);
        using var g = Drawing.Graphics.FromImage(result);
        g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.DrawImage(source, 0, 0, w, h);
        return result;
    }

    /// <summary>PNG с длинной стороной не больше maxSide (для отправки в ИИ).</summary>
    public static byte[] ToPngWithin(Drawing.Bitmap source, int maxSide)
    {
        int longSide = Math.Max(source.Width, source.Height);
        if (longSide <= maxSide) return ToPng(source);
        using var scaled = Scale(source, (double)maxSide / longSide);
        return ToPng(scaled);
    }
}
