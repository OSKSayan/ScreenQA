using System;
using ScreenQA.Native;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ScreenQA.Services;

/// <summary>Значок в области уведомлений — основная точка входа приложения.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Drawing.Icon _image;
    private readonly Forms.ToolStripMenuItem _captureItem;
    private readonly Forms.ToolStripMenuItem _pauseItem;

    public TrayIcon()
    {
        _image = CreateIcon();

        _captureItem = new Forms.ToolStripMenuItem("Выделить область", null, (_, _) => CaptureRequested?.Invoke());
        _pauseItem = new Forms.ToolStripMenuItem("Приостановить горячую клавишу");
        _pauseItem.Click += (_, _) => PauseToggled?.Invoke(!_pauseItem.Checked);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_captureItem);
        menu.Items.Add(new Forms.ToolStripMenuItem("Настройки…", null, (_, _) => SettingsRequested?.Invoke()));
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Открыть папку журнала", null, (_, _) => LogsRequested?.Invoke()));
        menu.Items.Add(new Forms.ToolStripMenuItem("Выход", null, (_, _) => ExitRequested?.Invoke()));

        _icon = new Forms.NotifyIcon
        {
            Icon = _image,
            Text = "ScreenQA",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => SettingsRequested?.Invoke();
    }

    public event Action? CaptureRequested;
    public event Action? SettingsRequested;
    public event Action? LogsRequested;
    public event Action? ExitRequested;
    public event Action<bool>? PauseToggled;

    public void Update(string hotkeyText, bool active, bool paused)
    {
        _captureItem.Text = $"Выделить область ({hotkeyText})";
        _pauseItem.Checked = paused;
        var state = paused ? "на паузе" : active ? hotkeyText : "клавиша не активна";
        var text = $"ScreenQA — {state}";
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void ShowBalloon(string title, string text, bool warning = false) =>
        _icon.ShowBalloonTip(4000, title, text, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);

    private static Drawing.Icon CreateIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var path = new Drawing.Drawing2D.GraphicsPath();
            const int r = 8;
            path.AddArc(1, 1, r * 2, r * 2, 180, 90);
            path.AddArc(31 - r * 2, 1, r * 2, r * 2, 270, 90);
            path.AddArc(31 - r * 2, 31 - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(1, 31 - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            using var bg = new Drawing.SolidBrush(Drawing.Color.FromArgb(59, 91, 219));
            g.FillPath(bg, path);
            using var font = new Drawing.Font("Segoe UI", 19, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            using var format = new Drawing.StringFormat
            {
                Alignment = Drawing.StringAlignment.Center,
                LineAlignment = Drawing.StringAlignment.Center,
            };
            g.DrawString("Q", font, Drawing.Brushes.White, new Drawing.RectangleF(0, 1, 32, 32), format);
        }

        IntPtr handle = bmp.GetHicon();
        try
        {
            using var temp = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)temp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }
}
