using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenQA.Native;
using ScreenQA.Services;
using Drawing = System.Drawing;

namespace ScreenQA.Views;

/// <summary>
/// Окно выделения на одном мониторе. Показывает снимок монитора, затемнённый поверх,
/// и прямоугольник выделения — как «Ножницы» (Win+Shift+S).
/// </summary>
public partial class SelectionOverlay : Window
{
    private const double MinSelection = 6;

    private readonly RectangleGeometry _full = new();
    private readonly RectangleGeometry _hole = new(Rect.Empty);
    private readonly bool _hideFromCapture;
    private Point? _start;

    internal SelectionOverlay(MonitorCapture capture, bool hideFromCapture)
    {
        InitializeComponent();
        Capture = capture;
        _hideFromCapture = hideFromCapture;
        // Программы записи рисуют курсор сами, поэтому крестик был бы виден даже при скрытом окне.
        if (hideFromCapture) Cursor = Cursors.Arrow;

        Snapshot.Source = ImageUtil.ToBitmapSource(capture.Image);
        Dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, _full, _hole);
        Root.SizeChanged += (_, _) => _full.Rect = new Rect(0, 0, Root.ActualWidth, Root.ActualHeight);

        MouseLeftButtonDown += OnLeftDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnLeftUp;
        MouseRightButtonUp += (_, _) => Cancelled?.Invoke();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Cancelled?.Invoke();
        };
    }

    internal MonitorCapture Capture { get; }

    /// <summary>Выделенная область в пикселях снимка этого монитора.</summary>
    internal event Action<SelectionOverlay, Drawing.Rectangle>? RegionSelected;

    internal event Action? Cancelled;

    internal void ShowOnMonitor()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (_hideFromCapture) CaptureProtection.Apply(this, true);

        // Позиционируем в физических пикселях. Повторный вызов после Show исправляет
        // размер, если WPF пересчитал окно при переходе на монитор с другим DPI.
        Place(hwnd);
        Show();
        Place(hwnd);
    }

    private void Place(IntPtr hwnd)
    {
        var b = Capture.Bounds;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, NativeMethods.SWP_NOACTIVATE);
    }

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        _start = Clamp(e.GetPosition(Root));
        CaptureMouse();
        Hint.Visibility = Visibility.Collapsed;
        UpdateSelection(_start.Value);
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_start is null) return;
        UpdateSelection(Clamp(e.GetPosition(Root)));
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is null) return;
        ReleaseMouseCapture();
        var rect = new Rect(_start.Value, Clamp(e.GetPosition(Root)));
        _start = null;

        if (rect.Width < MinSelection || rect.Height < MinSelection)
        {
            ResetSelection();
            return;
        }

        RegionSelected?.Invoke(this, ToPixels(rect));
    }

    private void UpdateSelection(Point current)
    {
        var r = new Rect(_start!.Value, current);
        _hole.Rect = r;

        foreach (var shape in new FrameworkElement[] { SelectionBorder, SelectionAccent })
        {
            Canvas.SetLeft(shape, r.X);
            Canvas.SetTop(shape, r.Y);
            shape.Width = r.Width;
            shape.Height = r.Height;
            shape.Visibility = Visibility.Visible;
        }

        var px = ToPixels(r);
        SizeText.Text = $"{px.Width} × {px.Height}";
        SizeBadge.Visibility = Visibility.Visible;
        SizeBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double top = r.Y - SizeBadge.DesiredSize.Height - 6;
        if (top < 4) top = r.Bottom + 6;
        Canvas.SetLeft(SizeBadge, r.X);
        Canvas.SetTop(SizeBadge, top);
    }

    private void ResetSelection()
    {
        _hole.Rect = Rect.Empty;
        SelectionBorder.Visibility = SelectionAccent.Visibility = SizeBadge.Visibility = Visibility.Collapsed;
        Hint.Visibility = Visibility.Visible;
    }

    private Point Clamp(Point p) =>
        new(Math.Clamp(p.X, 0, Root.ActualWidth), Math.Clamp(p.Y, 0, Root.ActualHeight));

    private Drawing.Rectangle ToPixels(Rect r)
    {
        // Снимок растянут на всё окно, поэтому DIP → пиксели снимка — простое отношение размеров.
        double sx = Capture.Image.Width / Math.Max(1, Root.ActualWidth);
        double sy = Capture.Image.Height / Math.Max(1, Root.ActualHeight);
        int x = (int)Math.Floor(r.X * sx);
        int y = (int)Math.Floor(r.Y * sy);
        int right = Math.Min(Capture.Image.Width, (int)Math.Ceiling(r.Right * sx));
        int bottom = Math.Min(Capture.Image.Height, (int)Math.Ceiling(r.Bottom * sy));
        return new Drawing.Rectangle(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
    }
}
