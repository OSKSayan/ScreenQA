using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using ScreenQA.Native;

namespace ScreenQA.Services;

/// <summary>Одно сочетание: модификаторы MOD_* и виртуальный код клавиши.</summary>
internal readonly record struct HotkeyBinding(uint Modifiers, int Key)
{
    public bool HasModifiers =>
        (Modifiers & (NativeMethods.MOD_ALT | NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_WIN)) != 0;

    public override string ToString() => HotkeyFormatter.Format(Modifiers, Key);
}

/// <summary>
/// Глобальные горячие клавиши через RegisterHotKey (без низкоуровневых перехватчиков клавиатуры).
/// Можно задать несколько сочетаний — срабатывает любое. Регистрация временно снимается, когда:
///  • пользователь поставил паузу (все сочетания);
///  • в окне ScreenQA идёт ввод текста (только сочетания без модификаторов, иначе нельзя набрать «q»);
///  • в настройках записывается новое сочетание (все сочетания).
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    private const int BaseId = 0x5141;

    private readonly HwndSource _source;
    private readonly List<HotkeyBinding> _bindings = new();
    private readonly HashSet<int> _registered = new();
    private bool _paused;
    private bool _typingSuspended;
    private bool _captureSuspended;

    public HotkeyService()
    {
        var parameters = new HwndSourceParameters("ScreenQA.Hotkey")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = NativeMethods.HWND_MESSAGE,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public event EventHandler? Pressed;
    public event EventHandler? StateChanged;

    public IReadOnlyList<HotkeyBinding> Bindings => _bindings;
    public bool IsActive => _registered.Count > 0;

    public string DisplayText => _bindings.Count == 0 ? "не задана" : string.Join(" или ", _bindings);

    public bool Paused
    {
        get => _paused;
        set { _paused = value; Refresh(); }
    }

    public bool TypingSuspended
    {
        get => _typingSuspended;
        set { if (_typingSuspended == value) return; _typingSuspended = value; Refresh(); }
    }

    public bool CaptureSuspended
    {
        get => _captureSuspended;
        set { if (_captureSuspended == value) return; _captureSuspended = value; Refresh(); }
    }

    /// <summary>Регистрирует новый набор сочетаний. Если хоть одно занято — остаётся прежний набор.</summary>
    public bool TrySetHotkeys(IReadOnlyList<HotkeyBinding> bindings, out string error)
    {
        var previous = _bindings.ToList();
        UnregisterAll();

        for (int i = 0; i < bindings.Count; i++)
        {
            var b = bindings[i];
            if (NativeMethods.RegisterHotKey(_source.Handle, BaseId + i, b.Modifiers | NativeMethods.MOD_NOREPEAT, (uint)b.Key))
            {
                _registered.Add(i);
                continue;
            }

            int code = Marshal.GetLastWin32Error();
            error = code == NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED
                ? $"Сочетание «{b}» уже занято Windows или другой программой. Выберите другое."
                : $"Не удалось зарегистрировать «{b}» (код ошибки {code}).";
            Log.Warn($"RegisterHotKey failed for {b}: {code}");

            UnregisterAll();
            _bindings.Clear();
            _bindings.AddRange(previous);
            Refresh();
            return false;
        }

        _bindings.Clear();
        _bindings.AddRange(bindings);
        error = string.Empty;
        Log.Info($"Горячие клавиши: {DisplayText}");
        Refresh();
        return true;
    }

    private void Refresh()
    {
        for (int i = 0; i < _bindings.Count; i++)
        {
            var b = _bindings[i];
            bool want = !_paused && !_captureSuspended && !(_typingSuspended && !b.HasModifiers);
            bool has = _registered.Contains(i);

            if (want && !has)
            {
                if (NativeMethods.RegisterHotKey(_source.Handle, BaseId + i, b.Modifiers | NativeMethods.MOD_NOREPEAT, (uint)b.Key))
                    _registered.Add(i);
                else
                    Log.Warn($"Повторная регистрация «{b}» не удалась: {Marshal.GetLastWin32Error()}");
            }
            else if (!want && has)
            {
                NativeMethods.UnregisterHotKey(_source.Handle, BaseId + i);
                _registered.Remove(i);
            }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UnregisterAll()
    {
        foreach (var i in _registered) NativeMethods.UnregisterHotKey(_source.Handle, BaseId + i);
        _registered.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id >= BaseId && id < BaseId + _bindings.Count)
            {
                handled = true;
                Pressed?.Invoke(this, EventArgs.Empty);
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}

internal static class HotkeyFormatter
{
    public static string Format(uint modifiers, int virtualKey)
    {
        if (virtualKey == 0) return "не задана";
        var parts = new List<string>();
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & NativeMethods.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(KeyName(virtualKey));
        return string.Join(" + ", parts);
    }

    public static string KeyName(int virtualKey)
    {
        var key = KeyInterop.KeyFromVirtualKey(virtualKey);
        return key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (key - Key.NumPad0),
            Key.Space => "Пробел",
            Key.Oem3 => "`",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.Oem2 => "/",
            Key.Oem5 => "\\",
            Key.Oem1 => ";",
            Key.Oem7 => "'",
            Key.OemOpenBrackets => "[",
            Key.Oem6 => "]",
            Key.Snapshot => "PrintScreen",
            Key.Pause => "Pause",
            Key.Insert => "Insert",
            Key.Home => "Home",
            Key.End => "End",
            _ => key.ToString(),
        };
    }
}
