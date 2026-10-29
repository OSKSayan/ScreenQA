using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using ScreenQA.Native;
using ScreenQA.Services;
using ScreenQA.Services.Ai;
using Drawing = System.Drawing;

namespace ScreenQA.Views;

/// <summary>Небольшое окно с ответом. Одно на всё приложение: при закрытии скрывается.</summary>
public partial class AnswerWindow : Window
{
    private Drawing.Bitmap? _snapshot;
    private AiAnswer? _answer;

    public AnswerWindow()
    {
        InitializeComponent();
    }

    internal bool AllowClose { get; set; }

    internal event Action<string>? AskAgainRequested;
    internal event Action? SettingsRequested;
    internal event Action? Dismissed;

    /// <summary>Новое выделение: окно забирает снимок (для кнопки «Сохранить») и очищает прошлый результат.</summary>
    internal void BeginNew(Drawing.Bitmap snapshot)
    {
        _snapshot?.Dispose();
        _snapshot = snapshot;
        _answer = null;
        ResultPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        OcrTextBox.Text = string.Empty;
        OcrExpander.IsExpanded = false;
        CopyButton.IsEnabled = false;
        SaveImageButton.IsEnabled = true;
    }

    internal void ShowBusy(string status)
    {
        StatusText.Text = status;
        BusyPanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        AskAgainButton.IsEnabled = false;
    }

    internal void ShowError(string message, bool offerSettings)
    {
        BusyPanel.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        OpenSettingsButton.Visibility = offerSettings ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        AskAgainButton.IsEnabled = true;
        if (!string.IsNullOrWhiteSpace(OcrTextBox.Text)) OcrExpander.IsExpanded = true;
    }

    internal void ShowAnswer(AiAnswer answer)
    {
        _answer = answer;
        BusyPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        QuestionText.Text = string.IsNullOrWhiteSpace(answer.Question) ? "—" : answer.Question;
        AnswerText.Text = answer.Answer;
        ExplanationText.Text = answer.Explanation;
        ExplanationCard.Visibility = string.IsNullOrWhiteSpace(answer.Explanation) ? Visibility.Collapsed : Visibility.Visible;
        ResultPanel.Visibility = Visibility.Visible;
        CopyButton.IsEnabled = true;
        AskAgainButton.IsEnabled = true;
    }

    internal void SetRecognizedText(string text) => OcrTextBox.Text = text;

    internal void SetCaptureState(CaptureProtectionState state)
    {
        CaptureText.Text = CaptureProtection.ShortText(state);
        CaptureChip.ToolTip = CaptureProtection.LongText(state) +
                              "\n\nВнимание: защита работает не со всеми программами записи (см. Настройки → Приватность).";
        var (dot, bg) = state switch
        {
            CaptureProtectionState.Hidden => ("#2B8A3E", "#EBFBEE"),
            CaptureProtectionState.BlackedOut => ("#B35C00", "#FFF4E6"),
            CaptureProtectionState.Failed => ("#C92A2A", "#FFF5F5"),
            _ => ("#868E96", "#E9ECEF"),
        };
        CaptureDot.Fill = (Brush)new BrushConverter().ConvertFromString(dot)!;
        CaptureChip.Background = (Brush)new BrushConverter().ConvertFromString(bg)!;
    }

    /// <summary>Ставит окно рядом с выделенной областью, не выходя за рабочую область монитора.</summary>
    internal void ShowNear(Drawing.Rectangle selection, Drawing.Rectangle workArea)
    {
        if (!IsVisible) Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!NativeMethods.GetWindowRect(hwnd, out var r)) return;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        const int gap = 12;

        int x = selection.Right + gap;
        if (x + w > workArea.Right) x = selection.Left - gap - w;
        if (x < workArea.Left) x = workArea.Right - w - gap;
        int y = Math.Clamp(selection.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - h));

        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private void OnAskAgain(object sender, RoutedEventArgs e)
    {
        var text = OcrTextBox.Text.Trim();
        if (text.Length == 0)
        {
            ShowError("Введите или исправьте текст вопроса, затем нажмите «Спросить ещё раз».", false);
            return;
        }
        AskAgainRequested?.Invoke(text);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_answer is null) return;
        var text = string.IsNullOrWhiteSpace(_answer.Explanation)
            ? _answer.Answer
            : _answer.Answer + Environment.NewLine + Environment.NewLine + _answer.Explanation;
        try
        {
            Clipboard.SetText(text);
            CopyButton.Content = "Скопировано ✓";
        }
        catch (Exception ex)
        {
            Log.Warn($"Буфер обмена недоступен: {ex.GetType().Name}");
            CopyButton.Content = "Буфер занят, ещё раз";
        }
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { CopyButton.Content = "Копировать ответ"; timer.Stop(); };
        timer.Start();
    }

    private void OnSaveImage(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить выделенную область",
            Filter = "Изображение PNG|*.png",
            FileName = $"ScreenQA-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, ImageUtil.ToPng(_snapshot));
            Log.Info("Снимок сохранён пользователем");
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось сохранить снимок", ex);
            MessageBox.Show(this, "Не удалось сохранить файл: " + ex.Message, "ScreenQA", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void OnClose(object sender, RoutedEventArgs e) => Dismiss();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Dismiss();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _snapshot?.Dispose();
        _snapshot = null;
        base.OnClosed(e);
    }

    /// <summary>Скрыть окно и забыть снимок — в памяти ничего не остаётся.</summary>
    private void Dismiss()
    {
        Hide();
        _snapshot?.Dispose();
        _snapshot = null;
        Dismissed?.Invoke();
    }
}
