using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ScreenQA.Services;

internal static class ProviderIds
{
    public const string ClaudeCode = "claudecode";
    public const string Anthropic = "anthropic";
    public const string OpenAiCompatible = "openai";
}

internal static class ClaudeCodeDefaults
{
    public const string Model = "claude-opus-5-5";

    // high — Opus думает дольше и перепроверяет решение; у Opus 5.5 по умолчанию medium.
    public const string Effort = "high";

    public static readonly string[] Efforts = { "low", "medium", "high", "xhigh", "max" };
}

internal sealed class AppSettings
{
    // Горячие клавиши: срабатывает любая из них. По умолчанию — Q без модификаторов.
    public List<HotkeySetting> Hotkeys { get; set; } = new() { new HotkeySetting { Modifiers = 0, Key = 0x51 } };

    // Тег языка Windows OCR ("ru", "en-US" …). Пусто — языки профиля Windows.
    public string OcrLanguage { get; set; } = string.Empty;

    public string Provider { get; set; } = ProviderIds.ClaudeCode;

    // Claude Code CLI: модель, тщательность рассуждения (--effort) и путь к claude.exe (пусто — найти автоматически).
    public string ClaudeCodeModel { get; set; } = ClaudeCodeDefaults.Model;
    public string ClaudeCodeEffort { get; set; } = ClaudeCodeDefaults.Effort;
    public string ClaudeCodePath { get; set; } = string.Empty;

    public string AnthropicModel { get; set; } = "claude-opus-5-5";
    public string AnthropicApiKeyProtected { get; set; } = string.Empty;

    public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string OpenAiModel { get; set; } = string.Empty;
    public string OpenAiApiKeyProtected { get; set; } = string.Empty;

    public string AnswerLanguage { get; set; } = "русский";

    // Отправлять картинку выделения в ИИ вместе с распознанным текстом (можно выключить в настройках).
    public bool SendImageToAi { get; set; } = true;

    // Скрывать окно ответа и окно настроек от записи / демонстрации экрана.
    public bool HideFromCapture { get; set; } = true;

    // Скрывать от записи и само затемнение выделения (в записи будет видна страница под ним и обычный курсор).
    public bool HideOverlayFromCapture { get; set; } = true;

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.Hotkeys = Hotkeys.Select(h => new HotkeySetting { Modifiers = h.Modifiers, Key = h.Key }).ToList();
        return copy;
    }

    public List<HotkeyBinding> GetHotkeyBindings() =>
        (Hotkeys ?? new List<HotkeySetting>())
        .Where(h => h.Key != 0)
        .Select(h => new HotkeyBinding(h.Modifiers, h.Key))
        .Distinct()
        .ToList();
}

/// <summary>Одно сочетание в settings.json: модификаторы MOD_* и виртуальный код клавиши.</summary>
internal sealed class HotkeySetting
{
    public uint Modifiers { get; set; }
    public int Key { get; set; }
}

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenQA");

    public string FilePath => Path.Combine(Directory, "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось прочитать настройки, используются значения по умолчанию", ex);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
