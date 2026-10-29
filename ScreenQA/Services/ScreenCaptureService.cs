using System;
using System.Collections.Generic;
using Drawing = System.Drawing;
using DrawingImaging = System.Drawing.Imaging;
using Forms = System.Windows.Forms;

namespace ScreenQA.Services;

/// <summary>Снимок одного монитора. Живёт только в памяти, пока открыт экран выделения.</summary>
internal sealed class MonitorCapture : IDisposable
{
    public MonitorCapture(Drawing.Rectangle bounds, Drawing.Rectangle workingArea, Drawing.Bitmap image)
    {
        Bounds = bounds;
        WorkingArea = workingArea;
        Image = image;
    }

    /// <summary>Границы монитора в физических пикселях виртуального экрана.</summary>
    public Drawing.Rectangle Bounds { get; }
    public Drawing.Rectangle WorkingArea { get; }
    public Drawing.Bitmap Image { get; }

    public void Dispose() => Image.Dispose();
}

internal static class ScreenCaptureService
{
    /// <summary>
    /// Делает снимок всех мониторов. Вызывается ТОЛЬКО по нажатию горячей клавиши,
    /// чтобы показать «замороженный» экран для выделения. На диск ничего не пишется.
    /// </summary>
    public static List<MonitorCapture> CaptureAllMonitors()
    {
        var result = new List<MonitorCapture>();
        try
        {
            foreach (var screen in Forms.Screen.AllScreens)
            {
                var b = screen.Bounds;
                var bmp = new Drawing.Bitmap(b.Width, b.Height, DrawingImaging.PixelFormat.Format32bppArgb);
                try
                {
                    using var g = Drawing.Graphics.FromImage(bmp);
                    g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size, Drawing.CopyPixelOperation.SourceCopy);
                }
                catch
                {
                    bmp.Dispose();
                    throw;
                }
                result.Add(new MonitorCapture(b, screen.WorkingArea, bmp));
            }
            return result;
        }
        catch (Exception ex)
        {
            foreach (var c in result) c.Dispose();
            throw new UserFacingException(
                "Не удалось получить изображение экрана. Это бывает на экране блокировки, в окнах UAC и в некоторых защищённых программах.",
                inner: ex);
        }
    }
}
