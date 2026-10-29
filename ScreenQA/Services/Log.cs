using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ScreenQA.Services;

/// <summary>
/// Простой файловый журнал. Всё, что пишется, проходит через <see cref="Redact"/>,
/// поэтому ключи API и заголовки авторизации в журнал не попадают.
/// Распознанный текст и ответы ИИ не журналируются — только их длина.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly List<string> Secrets = new();

    private static readonly Regex[] Patterns =
    {
        new(@"(?i)\b(sk-(?:ant-)?)[A-Za-z0-9_\-]{8,}", RegexOptions.Compiled),
        new(@"(?i)(bearer\s+)[^\s""',;]+", RegexOptions.Compiled),
        new(@"(?i)((?:x-api-key|api[_-]?key|authorization)[""']?\s*[:=]\s*[""']?)[^\s""',;]+", RegexOptions.Compiled),
    };

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenQA", "logs");

    private static string LogFile => Path.Combine(LogDirectory, "screenqa.log");

    /// <summary>Запомнить секрет, чтобы вырезать его из любых сообщений журнала.</summary>
    public static void RegisterSecret(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 6) return;
        lock (Gate)
        {
            if (!Secrets.Contains(secret)) Secrets.Add(secret);
        }
    }

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        lock (Gate)
        {
            foreach (var s in Secrets) text = text.Replace(s, "[скрыто]", StringComparison.Ordinal);
        }
        foreach (var p in Patterns) text = p.Replace(text, "$1[скрыто]");
        return text;
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var file = new FileInfo(LogFile);
                if (file.Exists && file.Length > MaxBytes)
                {
                    File.Copy(LogFile, LogFile + ".1", overwrite: true);
                    File.Delete(LogFile);
                }
            }
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {Redact(message)}{Environment.NewLine}";
            lock (Gate) File.AppendAllText(LogFile, line);
        }
        catch
        {
            // Журнал не должен ронять приложение.
        }
    }
}
