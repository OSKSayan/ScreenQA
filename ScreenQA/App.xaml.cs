using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ScreenQA.Services;
using ScreenQA.Services.Ai;
using ScreenQA.Views;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ScreenQA;

public partial class App : Application
{
    private static readonly TimeSpan AiTimeout = TimeSpan.FromMinutes(2);

    private Mutex? _singleInstance;
    private TrayIcon? _tray;
    private AnswerWindow? _answer;
    private SettingsWindow? _settingsWindow;
    private List<SelectionOverlay>? _overlays;
    private List<MonitorCapture>? _captures;
    private CancellationTokenSource? _cts;
    private byte[]? _lastPng;
    private bool _selecting;
    private bool _appActive;
    private bool _answerWasVisible;

    internal SettingsStore Store { get; } = new();
    internal AppSettings Settings { get; private set; } = new();
    internal HotkeyService Hotkeys { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\ScreenQA.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("ScreenQA уже запущен — значок находится в области уведомлений (рядом с часами).",
                "ScreenQA", MessageBoxButton.OK, MessageBoxImage.Information);
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Необработанное исключение", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Необработанное исключение в задаче", args.Exception);
            args.SetObserved();
        };

        Settings = Store.Load();
        Log.RegisterSecret(SecretProtector.Unprotect(Settings.AnthropicApiKeyProtected));
        Log.RegisterSecret(SecretProtector.Unprotect(Settings.OpenAiApiKeyProtected));
        Log.Info($"Запуск ScreenQA {typeof(App).Assembly.GetName().Version}, Windows {Environment.OSVersion.Version}");

        Hotkeys = new HotkeyService();
        Hotkeys.Pressed += (_, _) => _ = StartSelectionAsync();
        Hotkeys.StateChanged += (_, _) => UpdateTray();

        // Если горячая клавиша без модификаторов, при вводе текста в окнах ScreenQA её временно снимаем.
        Activated += (_, _) => { _appActive = true; QueueTypingCheck(); };
        Deactivated += (_, _) => { _appActive = false; QueueTypingCheck(); };
        foreach (var type in new[] { typeof(TextBox), typeof(PasswordBox) })
        {
            EventManager.RegisterClassHandler(type, UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, _) => QueueTypingCheck()));
            EventManager.RegisterClassHandler(type, UIElement.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, _) => QueueTypingCheck()));
        }

        _tray = new TrayIcon();
        _tray.CaptureRequested += () => _ = StartSelectionAsync();
        _tray.SettingsRequested += OpenSettings;
        _tray.LogsRequested += OpenLogFolder;
        _tray.PauseToggled += paused => Hotkeys.Paused = paused;
        _tray.ExitRequested += ExitApp;

        if (Hotkeys.TrySetHotkeys(Settings.GetHotkeyBindings(), out var error))
        {
            _tray.ShowBalloon("ScreenQA работает",
                $"Нажмите {Hotkeys.DisplayText}, чтобы выделить вопрос на экране. Настройки — в значке у часов.");
        }
        else
        {
            _tray.ShowBalloon("Горячая клавиша не работает", error, warning: true);
            OpenSettings();
        }
        UpdateTray();

        if (e.Args.Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase))) OpenSettings();
    }

    // ---------- Выделение области ----------

    internal async Task StartSelectionAsync()
    {
        if (_selecting) return;
        _selecting = true;
        try
        {
            // Окно ответа убираем, чтобы оно не закрывало текст под собой.
            _answerWasVisible = _answer?.IsVisible == true;
            if (_answerWasVisible)
            {
                _answer!.Hide();
                await Task.Delay(150);
            }

            _captures = ScreenCaptureService.CaptureAllMonitors();
            _overlays = new List<SelectionOverlay>();
            foreach (var capture in _captures)
            {
                var overlay = new SelectionOverlay(capture, Settings.HideOverlayFromCapture);
                overlay.RegionSelected += OnRegionSelected;
                overlay.Cancelled += OnSelectionCancelled;
                _overlays.Add(overlay);
            }
            foreach (var overlay in _overlays) overlay.ShowOnMonitor();

            var cursor = Forms.Cursor.Position;
            var target = _overlays.FirstOrDefault(o => o.Capture.Bounds.Contains(cursor)) ?? _overlays.First();
            target.Activate();
            target.Focus();
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось открыть экран выделения", ex);
            CloseOverlays();
            var win = EnsureAnswerWindow();
            win.ShowError(ex is UserFacingException ufe ? ufe.Message : "Не удалось открыть экран выделения: " + ex.Message, false);
            if (!win.IsVisible) win.Show();
        }
    }

    private void OnRegionSelected(SelectionOverlay overlay, Drawing.Rectangle region)
    {
        var capture = overlay.Capture;
        Drawing.Bitmap crop;
        try
        {
            crop = capture.Image.Clone(region, capture.Image.PixelFormat);
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось вырезать область", ex);
            CloseOverlays();
            return;
        }

        var onScreen = new Drawing.Rectangle(capture.Bounds.X + region.X, capture.Bounds.Y + region.Y, region.Width, region.Height);
        var workArea = capture.WorkingArea;
        CloseOverlays();
        _ = ProcessSelectionAsync(crop, onScreen, workArea);
    }

    private void OnSelectionCancelled()
    {
        CloseOverlays();
        if (_answerWasVisible) _answer?.Show();
    }

    private void CloseOverlays()
    {
        if (_overlays is not null)
        {
            foreach (var o in _overlays)
            {
                o.RegionSelected -= OnRegionSelected;
                o.Cancelled -= OnSelectionCancelled;
                o.Close();
            }
            _overlays = null;
        }
        if (_captures is not null)
        {
            foreach (var c in _captures) c.Dispose();
            _captures = null;
        }
        _selecting = false;
    }

    // ---------- Распознавание и запрос к ИИ ----------

    private async Task ProcessSelectionAsync(Drawing.Bitmap crop, Drawing.Rectangle onScreen, Drawing.Rectangle workArea)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var win = EnsureAnswerWindow();
        // Картинку для ИИ готовим сразу — окно владеет crop и может освободить его при закрытии.
        _lastPng = Settings.SendImageToAi ? ImageUtil.ToPngWithin(crop, 1568) : null;
        Task<string> ocrTask;
        try
        {
            ocrTask = OcrService.RecognizeAsync(crop, Settings.OcrLanguage, ct);
        }
        catch (Exception ex)
        {
            ocrTask = Task.FromException<string>(ex);
        }

        win.BeginNew(crop);
        win.ShowBusy("Распознаю текст…");
        win.ShowNear(onScreen, workArea);

        string text;
        try
        {
            text = await ocrTask;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (UserFacingException ex)
        {
            if (!ct.IsCancellationRequested) win.ShowError(ex.Message, ex.OfferSettings);
            return;
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка OCR", ex);
            if (!ct.IsCancellationRequested) win.ShowError("Не удалось распознать текст: " + ex.Message, false);
            return;
        }
        if (ct.IsCancellationRequested) return;

        Log.Info($"OCR: распознано {text.Length} символов");
        win.SetRecognizedText(text);

        if (string.IsNullOrWhiteSpace(text) && _lastPng is null)
        {
            win.ShowError("В выделенной области не найден текст. Выделите вопрос крупнее или проверьте язык распознавания в настройках.", true);
            return;
        }

        await AskAsync(text, ct);
    }

    private async Task AskAsync(string text, CancellationToken ct)
    {
        var win = EnsureAnswerWindow();

        IAiProvider provider;
        try
        {
            provider = AiProviderFactory.Create(Settings);
        }
        catch (UserFacingException ex)
        {
            win.ShowError(ex.Message, ex.OfferSettings);
            return;
        }

        win.ShowBusy($"Спрашиваю {provider.DisplayName}…");
        var image = Settings.SendImageToAi ? _lastPng : null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(AiTimeout);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var raw = await provider.CompleteAsync(
                StudyPrompt.BuildSystem(Settings.AnswerLanguage),
                StudyPrompt.BuildUser(text, image is not null),
                image,
                timeout.Token);
            if (ct.IsCancellationRequested) return;
            Log.Info($"Ответ ИИ получен за {stopwatch.ElapsedMilliseconds} мс, {raw.Length} символов");
            win.ShowAnswer(StudyPrompt.Parse(raw));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Пользователь закрыл окно или начал новое выделение.
        }
        catch (OperationCanceledException)
        {
            win.ShowError("ИИ не ответил за 2 минуты. Проверьте соединение и попробуйте ещё раз.", false);
        }
        catch (UserFacingException ex)
        {
            Log.Warn("Запрос к ИИ: " + ex.Message);
            if (!ct.IsCancellationRequested) win.ShowError(ex.Message, ex.OfferSettings);
        }
        catch (Exception ex)
        {
            Log.Error("Запрос к ИИ", ex);
            if (!ct.IsCancellationRequested) win.ShowError("Ошибка запроса к ИИ: " + Log.Redact(ex.Message), false);
        }
    }

    private AnswerWindow EnsureAnswerWindow()
    {
        if (_answer is not null) return _answer;

        _answer = new AnswerWindow();
        _answer.SourceInitialized += (_, _) => _answer.SetCaptureState(CaptureProtection.Apply(_answer, Settings.HideFromCapture));
        _answer.AskAgainRequested += text =>
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _ = AskAsync(text, _cts.Token);
        };
        _answer.SettingsRequested += OpenSettings;
        _answer.Dismissed += () =>
        {
            _cts?.Cancel();
            _lastPng = null;
        };
        return _answer;
    }

    // ---------- Настройки ----------

    internal void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            QueueTypingCheck();
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    internal bool TryApplySettings(AppSettings next, out string error, out bool hotkeyFailed)
    {
        hotkeyFailed = false;
        var bindings = next.GetHotkeyBindings();
        if (!bindings.SequenceEqual(Hotkeys.Bindings) || (!Hotkeys.IsActive && !Hotkeys.Paused))
        {
            // Поле ввода сочетания могло снять регистрацию — возвращаем, иначе проверка не имеет смысла.
            Hotkeys.CaptureSuspended = false;
            if (!Hotkeys.TrySetHotkeys(bindings, out error))
            {
                hotkeyFailed = true;
                return false;
            }
        }

        try
        {
            Store.Save(next);
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось сохранить настройки", ex);
            error = "Не удалось сохранить настройки: " + ex.Message;
            return false;
        }

        Settings = next;
        Log.Info("Настройки сохранены");

        if (_answer is not null && new System.Windows.Interop.WindowInteropHelper(_answer).Handle != IntPtr.Zero)
            _answer.SetCaptureState(CaptureProtection.Apply(_answer, Settings.HideFromCapture));

        UpdateTray();
        QueueTypingCheck();
        error = string.Empty;
        return true;
    }

    // ---------- Служебное ----------

    private void QueueTypingCheck() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            Hotkeys.TypingSuspended = _appActive && Keyboard.FocusedElement is TextBox { IsReadOnly: false } or PasswordBox;
        });

    private void UpdateTray() => _tray?.Update(Hotkeys.DisplayText, Hotkeys.IsActive, Hotkeys.Paused);

    private static void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(Log.LogDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Log.LogDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось открыть папку журнала", ex);
        }
    }

    private void OnUnhandledUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Необработанное исключение UI", e.Exception);
        e.Handled = true;
        MessageBox.Show("Произошла непредвиденная ошибка: " + Log.Redact(e.Exception.Message) +
                        "\n\nПодробности записаны в журнал (меню значка → «Открыть папку журнала»).",
            "ScreenQA", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ExitApp()
    {
        _cts?.Cancel();
        CloseOverlays();
        if (_answer is not null)
        {
            _answer.AllowClose = true;
            _answer.Close();
        }
        _settingsWindow?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("Выход");
        Hotkeys?.Dispose();
        _tray?.Dispose();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
