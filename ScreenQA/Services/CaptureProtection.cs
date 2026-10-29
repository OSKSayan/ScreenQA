using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ScreenQA.Native;

namespace ScreenQA.Services;

internal enum CaptureProtectionState
{
    /// <summary>Настройка выключена — окно видно в записи.</summary>
    Disabled,
    /// <summary>WDA_EXCLUDEFROMCAPTURE: окно полностью исключено из захвата.</summary>
    Hidden,
    /// <summary>WDA_MONITOR (старые Windows): в записи вместо окна чёрный прямоугольник.</summary>
    BlackedOut,
    /// <summary>Windows отказала в установке защиты.</summary>
    Failed,
}

/// <summary>
/// Обёртка над SetWindowDisplayAffinity. Работает для программ, которые захватывают экран
/// через стандартные API Windows (Windows.Graphics.Capture, DXGI Desktop Duplication, GDI).
/// Не гарантирует защиту от драйверного захвата, карт видеозахвата, удалённых сессий и съёмки камерой.
/// </summary>
internal static class CaptureProtection
{
    public static bool IsExcludeSupported => Environment.OSVersion.Version.Build >= 19041;

    public static CaptureProtectionState Apply(Window window, bool enable)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();

        if (!enable)
        {
            NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_NONE);
            return CaptureProtectionState.Disabled;
        }

        if (IsExcludeSupported
            && NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE)
            && NativeMethods.GetWindowDisplayAffinity(hwnd, out uint actual)
            && actual == NativeMethods.WDA_EXCLUDEFROMCAPTURE)
        {
            return CaptureProtectionState.Hidden;
        }

        if (NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_MONITOR))
        {
            return CaptureProtectionState.BlackedOut;
        }

        Log.Warn($"SetWindowDisplayAffinity failed: {Marshal.GetLastWin32Error()}");
        return CaptureProtectionState.Failed;
    }

    public static string ShortText(CaptureProtectionState state) => state switch
    {
        CaptureProtectionState.Hidden => "Скрыто от записи",
        CaptureProtectionState.BlackedOut => "Частично скрыто",
        CaptureProtectionState.Failed => "Защита не работает",
        _ => "Видно в записи",
    };

    public static string LongText(CaptureProtectionState state) => state switch
    {
        CaptureProtectionState.Hidden =>
            "Окно исключено из захвата экрана средствами Windows: в записи и демонстрации экрана на его месте будет видно то, что под ним.",
        CaptureProtectionState.BlackedOut =>
            "Эта версия Windows не умеет полностью исключать окна из захвата. В записи на месте окна будет чёрный прямоугольник.",
        CaptureProtectionState.Failed =>
            "Windows не позволила включить защиту (например, в сеансе удалённого рабочего стола или без композиции рабочего стола). Окно будет видно в записи.",
        _ => "Защита выключена: окно ответа будет видно в записи и демонстрации экрана.",
    };
}
