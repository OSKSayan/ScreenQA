using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScreenQA.Native;
using ScreenQA.Services;
using ScreenQA.Services.Ai;

namespace ScreenQA.Views;

public partial class SettingsWindow : Window
{
    private readonly App _app;
    private readonly List<HotkeyRow> _hotkeyRows = new();
    private bool _clearAnthropicKey;
    private bool _clearOpenAiKey;

    internal SettingsWindow(App app)
    {
        _app = app;
        InitializeComponent();
        SourceInitialized += (_, _) => ShowCaptureState(CaptureProtection.Apply(this, _app.Settings.HideFromCapture));
        Closed += (_, _) => _app.Hotkeys.CaptureSuspended = false;
        Load(_app.Settings);
    }

    private void Load(AppSettings s)
    {
        foreach (var b in s.GetHotkeyBindings()) AddHotkeyRow(b.Modifiers, b.Key, focus: false);
        if (_hotkeyRows.Count == 0) AddHotkeyRow(0, 0, focus: false);
        UpdateHotkeyWarning();

        // Языки OCR
        var languages = new List<OcrLanguageInfo> { new(string.Empty, "Автоматически — языки профиля Windows") };
        var available = OcrService.GetAvailableLanguages();
        languages.AddRange(available);
        if (!string.IsNullOrEmpty(s.OcrLanguage) && languages.All(l => !string.Equals(l.Tag, s.OcrLanguage, StringComparison.OrdinalIgnoreCase)))
            languages.Add(new OcrLanguageInfo(s.OcrLanguage, $"{s.OcrLanguage} — не установлен в Windows"));
        OcrLanguageBox.ItemsSource = languages;
        OcrLanguageBox.SelectedItem = languages.First(l => string.Equals(l.Tag, s.OcrLanguage, StringComparison.OrdinalIgnoreCase));
        if (available.Count == 0)
        {
            OcrInstallHint.Text = "В Windows не найдено ни одного языка распознавания. " + OcrService.InstallHint;
            OcrInstallHint.Visibility = Visibility.Visible;
        }
        else
        {
            OcrInstallHint.Text = "Нет нужного языка? " + OcrService.InstallHint;
            OcrInstallHint.Visibility = Visibility.Visible;
            OcrInstallHint.Foreground = (Brush)FindResource("MutedBrush");
        }

        // ИИ
        ProviderBox.SelectedItem = ProviderBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == s.Provider) ?? ProviderBox.Items[0];
        ClaudeCodeModelBox.Text = string.IsNullOrWhiteSpace(s.ClaudeCodeModel) ? ClaudeCodeDefaults.Model : s.ClaudeCodeModel;
        ClaudeCodeEffortBox.SelectedItem = ClaudeCodeEffortBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == s.ClaudeCodeEffort)
            ?? ClaudeCodeEffortBox.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == ClaudeCodeDefaults.Effort);
        ClaudeCodePathBox.Text = s.ClaudeCodePath;
        UpdateClaudeCodeState();
        AnthropicModelBox.Text = s.AnthropicModel;
        OpenAiUrlBox.Text = s.OpenAiBaseUrl;
        OpenAiModelBox.Text = s.OpenAiModel;
        AnswerLanguageBox.Text = s.AnswerLanguage;
        SendImageBox.IsChecked = s.SendImageToAi;
        UpdateKeyStates(s);

        // Приватность
        HideFromCaptureBox.IsChecked = s.HideFromCapture;
        HideOverlayBox.IsChecked = s.HideOverlayFromCapture;
    }

    private void UpdateKeyStates(AppSettings s)
    {
        bool hasAnthropic = !_clearAnthropicKey && !string.IsNullOrEmpty(s.AnthropicApiKeyProtected);
        bool envAnthropic = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
        AnthropicKeyState.Text = hasAnthropic
            ? "Ключ сохранён. Оставьте поле пустым, чтобы не менять его."
            : envAnthropic
                ? "Ключ не сохранён — будет использована переменная окружения ANTHROPIC_API_KEY."
                : "Ключ не задан. Получить ключ: console.anthropic.com → API Keys.";
        AnthropicKeyClear.IsEnabled = hasAnthropic;

        bool hasOpenAi = !_clearOpenAiKey && !string.IsNullOrEmpty(s.OpenAiApiKeyProtected);
        OpenAiKeyState.Text = hasOpenAi
            ? "Ключ сохранён. Оставьте поле пустым, чтобы не менять его."
            : "Ключ не задан. Для локальных Ollama / LM Studio ключ не нужен.";
        OpenAiKeyClear.IsEnabled = hasOpenAi;
    }

    // ---------- Горячие клавиши ----------

    private sealed class HotkeyRow
    {
        public uint Modifiers;
        public int Key;
        public TextBox Box = null!;
        public FrameworkElement Root = null!;
    }

    private const string EmptyHotkeyText = "Щёлкните и нажмите клавишу или сочетание…";

    private void AddHotkeyRow(uint modifiers, int key, bool focus)
    {
        var box = new TextBox
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            FontSize = 14,
            Cursor = Cursors.Hand,
            ToolTip = "Щёлкните и нажмите нужную клавишу или сочетание",
        };
        var remove = new Button { Content = "Удалить", Margin = new Thickness(8, 0, 0, 0) };
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(remove, Dock.Right);
        row.Children.Add(remove);
        row.Children.Add(box);

        var item = new HotkeyRow { Modifiers = modifiers, Key = key, Box = box, Root = row };
        box.Text = RowText(item);
        box.PreviewKeyDown += (_, e) => OnHotkeyKeyDown(item, e);
        box.GotKeyboardFocus += (_, _) => OnHotkeyFocus(item);
        box.LostKeyboardFocus += (_, _) => OnHotkeyBlur(item);
        remove.Click += (_, _) =>
        {
            _hotkeyRows.Remove(item);
            HotkeyList.Children.Remove(row);
            HotkeyError.Visibility = Visibility.Collapsed;
            UpdateHotkeyWarning();
        };

        _hotkeyRows.Add(item);
        HotkeyList.Children.Add(row);
        if (focus) box.Focus();
    }

    private static string RowText(HotkeyRow row) =>
        row.Key == 0 ? EmptyHotkeyText : HotkeyFormatter.Format(row.Modifiers, row.Key);

    private void OnAddHotkey(object sender, RoutedEventArgs e) => AddHotkeyRow(0, 0, focus: true);

    private void OnHotkeyFocus(HotkeyRow row)
    {
        // Пока поле в фокусе, глобальные клавиши сняты — чтобы можно было нажать и уже назначенное сочетание.
        _app.Hotkeys.CaptureSuspended = true;
        row.Box.Text = "Нажмите клавишу или сочетание… (Esc — отмена)";
    }

    private void OnHotkeyBlur(HotkeyRow row)
    {
        _app.Hotkeys.CaptureSuspended = false;
        row.Box.Text = RowText(row);
    }

    private void OnHotkeyKeyDown(HotkeyRow row, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        if (key == Key.Escape && mods == ModifierKeys.None)
        {
            Keyboard.ClearFocus();
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.None or Key.ImeProcessed or Key.DeadCharProcessed)
        {
            var held = string.Join(" + ", Describe(mods));
            row.Box.Text = held.Length > 0 ? held + " + …" : "…";
            return;
        }

        uint newMods = ToNative(mods);
        int newKey = KeyInterop.VirtualKeyFromKey(key);
        if (_hotkeyRows.Any(r => r != row && r.Modifiers == newMods && r.Key == newKey))
        {
            HotkeyError.Text = $"Сочетание «{HotkeyFormatter.Format(newMods, newKey)}» уже есть в списке.";
            HotkeyError.Visibility = Visibility.Visible;
            return;
        }

        row.Modifiers = newMods;
        row.Key = newKey;
        row.Box.Text = RowText(row);
        HotkeyError.Visibility = Visibility.Collapsed;
        UpdateHotkeyWarning();
        Keyboard.ClearFocus();
    }

    private static uint ToNative(ModifierKeys mods)
    {
        uint result = 0;
        if (mods.HasFlag(ModifierKeys.Control)) result |= NativeMethods.MOD_CONTROL;
        if (mods.HasFlag(ModifierKeys.Alt)) result |= NativeMethods.MOD_ALT;
        if (mods.HasFlag(ModifierKeys.Shift)) result |= NativeMethods.MOD_SHIFT;
        if (mods.HasFlag(ModifierKeys.Windows)) result |= NativeMethods.MOD_WIN;
        return result;
    }

    private static IEnumerable<string> Describe(ModifierKeys mods)
    {
        if (mods.HasFlag(ModifierKeys.Control)) yield return "Ctrl";
        if (mods.HasFlag(ModifierKeys.Alt)) yield return "Alt";
        if (mods.HasFlag(ModifierKeys.Shift)) yield return "Shift";
        if (mods.HasFlag(ModifierKeys.Windows)) yield return "Win";
    }

    private void UpdateHotkeyWarning()
    {
        HotkeyWarning.Visibility = _hotkeyRows.Any(r => r.Key != 0 && (r.Modifiers & 0xF) == 0)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ---------- Провайдер и ключи ----------

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AnthropicPanel is null || OpenAiPanel is null || ClaudeCodePanel is null) return; // ещё не построено
        var provider = SelectedProvider;
        ClaudeCodePanel.Visibility = provider == ProviderIds.ClaudeCode ? Visibility.Visible : Visibility.Collapsed;
        AnthropicPanel.Visibility = provider == ProviderIds.Anthropic ? Visibility.Visible : Visibility.Collapsed;
        OpenAiPanel.Visibility = provider == ProviderIds.OpenAiCompatible ? Visibility.Visible : Visibility.Collapsed;
    }

    private string SelectedProvider => (ProviderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? ProviderIds.ClaudeCode;

    private void OnClaudePathChanged(object sender, TextChangedEventArgs e) => UpdateClaudeCodeState();

    private void UpdateClaudeCodeState()
    {
        if (ClaudeCodeState is null) return;
        var configured = ClaudeCodePathBox.Text.Trim();
        var found = ClaudeCodeProvider.FindExecutable(configured);
        if (found is not null)
        {
            ClaudeCodeState.Text = (configured.Length == 0 ? "Найден автоматически: " : "Найден: ") + found;
            ClaudeCodeState.Foreground = (Brush)FindResource("OkBrush");
        }
        else
        {
            ClaudeCodeState.Text = configured.Length == 0
                ? "Claude Code не найден. Установите его или укажите путь к claude.exe."
                : "Файл не найден.";
            ClaudeCodeState.Foreground = (Brush)FindResource("DangerBrush");
        }
    }

    private void OnClearAnthropicKey(object sender, RoutedEventArgs e)
    {
        _clearAnthropicKey = true;
        AnthropicKeyBox.Clear();
        UpdateKeyStates(_app.Settings);
        AnthropicKeyState.Text = "Ключ будет удалён после нажатия «Сохранить».";
    }

    private void OnClearOpenAiKey(object sender, RoutedEventArgs e)
    {
        _clearOpenAiKey = true;
        OpenAiKeyBox.Clear();
        UpdateKeyStates(_app.Settings);
        OpenAiKeyState.Text = "Ключ будет удалён после нажатия «Сохранить».";
    }

    // ---------- Приватность ----------

    private void OnHideToggled(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || !IsLoaded) return;
        // Сразу применяем к этому окну — статус показывает реальный результат, а не обещание.
        ShowCaptureState(CaptureProtection.Apply(this, HideFromCaptureBox.IsChecked == true));
    }

    private void ShowCaptureState(CaptureProtectionState state)
    {
        CaptureStatusTitle.Text = CaptureProtection.ShortText(state);
        CaptureStatusText.Text = CaptureProtection.LongText(state);
        var (dot, bg) = state switch
        {
            CaptureProtectionState.Hidden => ("#2B8A3E", "#EBFBEE"),
            CaptureProtectionState.BlackedOut => ("#B35C00", "#FFF4E6"),
            CaptureProtectionState.Failed => ("#C92A2A", "#FFF5F5"),
            _ => ("#868E96", "#E9ECEF"),
        };
        var converter = new BrushConverter();
        CaptureStatusDot.Fill = (Brush)converter.ConvertFromString(dot)!;
        CaptureStatusPanel.Background = (Brush)converter.ConvertFromString(bg)!;
    }

    // ---------- Сохранение ----------

    private void OnSave(object sender, RoutedEventArgs e)
    {
        SaveError.Text = string.Empty;
        HotkeyError.Visibility = Visibility.Collapsed;

        var next = _app.Settings.Clone();
        next.Hotkeys = _hotkeyRows
            .Where(r => r.Key != 0)
            .Select(r => new HotkeySetting { Modifiers = r.Modifiers, Key = r.Key })
            .ToList();
        if (next.Hotkeys.Count == 0)
        {
            HotkeyError.Text = "Добавьте хотя бы одну клавишу или сочетание.";
            HotkeyError.Visibility = Visibility.Visible;
            SaveError.Text = "Горячие клавиши не заданы — см. выше.";
            return;
        }
        next.OcrLanguage = (OcrLanguageBox.SelectedItem as OcrLanguageInfo)?.Tag ?? string.Empty;
        next.Provider = SelectedProvider;
        next.ClaudeCodeModel = ClaudeCodeModelBox.Text.Trim();
        next.ClaudeCodeEffort = (ClaudeCodeEffortBox.SelectedItem as ComboBoxItem)?.Tag as string ?? ClaudeCodeDefaults.Effort;
        next.ClaudeCodePath = ClaudeCodePathBox.Text.Trim().Trim('"');
        next.AnthropicModel = AnthropicModelBox.Text.Trim();
        next.OpenAiBaseUrl = OpenAiUrlBox.Text.Trim();
        next.OpenAiModel = OpenAiModelBox.Text.Trim();
        next.AnswerLanguage = string.IsNullOrWhiteSpace(AnswerLanguageBox.Text) ? "русский" : AnswerLanguageBox.Text.Trim();
        next.SendImageToAi = SendImageBox.IsChecked == true;
        next.HideFromCapture = HideFromCaptureBox.IsChecked == true;
        next.HideOverlayFromCapture = HideOverlayBox.IsChecked == true;

        if (_clearAnthropicKey) next.AnthropicApiKeyProtected = string.Empty;
        if (_clearOpenAiKey) next.OpenAiApiKeyProtected = string.Empty;
        try
        {
            var anthropicKey = AnthropicKeyBox.Password.Trim();
            if (anthropicKey.Length > 0)
            {
                Log.RegisterSecret(anthropicKey);
                next.AnthropicApiKeyProtected = SecretProtector.Protect(anthropicKey);
            }
            var openAiKey = OpenAiKeyBox.Password.Trim();
            if (openAiKey.Length > 0)
            {
                Log.RegisterSecret(openAiKey);
                next.OpenAiApiKeyProtected = SecretProtector.Protect(openAiKey);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось зашифровать ключ", ex);
            SaveError.Text = "Не удалось зашифровать ключ средствами Windows. Ключ не сохранён.";
            return;
        }

        if (!_app.TryApplySettings(next, out var error, out bool hotkeyFailed))
        {
            if (hotkeyFailed)
            {
                HotkeyError.Text = error;
                HotkeyError.Visibility = Visibility.Visible;
                SaveError.Text = "Горячая клавиша не сохранена — см. выше.";
            }
            else
            {
                SaveError.Text = error;
            }
            return;
        }

        AnthropicKeyBox.Clear();
        OpenAiKeyBox.Clear();
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e) => OpenFolder(_app.Store.Directory);

    private void OnOpenLogFolder(object sender, RoutedEventArgs e) => OpenFolder(Log.LogDirectory);

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось открыть папку", ex);
        }
    }
}
