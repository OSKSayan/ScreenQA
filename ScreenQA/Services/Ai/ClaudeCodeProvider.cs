using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenQA.Services.Ai;

/// <summary>
/// Claude через установленный Claude Code CLI (<c>claude -p</c>) — работает по подписке Claude
/// (Pro / Max), ключ API не нужен. Текст и картинка передаются через stdin (stream-json),
/// на диск ничего не пишется; сессия не сохраняется в историю Claude Code; инструменты отключены,
/// поэтому Claude только отвечает и ничего не делает на компьютере.
/// </summary>
internal sealed class ClaudeCodeProvider : IAiProvider
{
    private readonly string _executable;
    private readonly string _model;
    private readonly string _effort;

    public ClaudeCodeProvider(string executable, string model, string effort)
    {
        _executable = executable;
        _model = model;
        _effort = effort;
    }

    public string DisplayName => $"Claude ({_model}, тщательность {_effort})";

    /// <summary>Ищет claude.exe: явный путь из настроек, затем PATH и стандартные места установки.</summary>
    public static string? FindExecutable(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured.Trim('"', ' ')) ? configured.Trim('"', ' ') : null;

        var candidates = new List<string>();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir.Trim('"'), "claude.exe"));
            candidates.Add(Path.Combine(dir.Trim('"'), "claude.cmd"));
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(home, ".local", "bin", "claude.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd"));

        foreach (var c in candidates)
        {
            try
            {
                if (File.Exists(c)) return c;
            }
            catch (Exception)
            {
                // кривой элемент PATH — пропускаем
            }
        }
        return null;
    }

    public async Task<string> CompleteAsync(string system, string user, byte[]? png, CancellationToken ct)
    {
        var workDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenQA", "claude-work");
        Directory.CreateDirectory(workDir);

        var args = new List<string>
        {
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--tools", "",
            "--no-session-persistence",
            "--safe-mode",
            "--strict-mcp-config",
            "--disable-slash-commands",
            "--system-prompt", "Ты внимательный репетитор. Отвечай строго по инструкциям в сообщении пользователя.",
        };
        if (!string.IsNullOrWhiteSpace(_model)) args.AddRange(new[] { "--model", _model });
        if (!string.IsNullOrWhiteSpace(_effort)) args.AddRange(new[] { "--effort", _effort });

        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (_executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(_executable);
        }
        else
        {
            psi.FileName = _executable;
        }
        foreach (var a in args) psi.ArgumentList.Add(a);

        // Работаем по подписке: ключ API из окружения не должен перехватить авторизацию.
        psi.Environment.Remove("ANTHROPIC_API_KEY");
        psi.Environment.Remove("ANTHROPIC_AUTH_TOKEN");
        // Если ScreenQA запущен из сессии Claude Code / приложения Claude, не наследуем её служебные
        // переменные (адрес прокси сессии, id сессии и т. п.) — CLI должен работать как из обычного терминала.
        if (psi.Environment.ContainsKey("CLAUDECODE"))
        {
            var inherited = psi.Environment.Keys
                .Where(k => k == "CLAUDECODE" || k == "CLAUDE_PID" || k == "ANTHROPIC_BASE_URL"
                            || k.StartsWith("CLAUDE_AGENT_SDK_", StringComparison.Ordinal)
                            || (k.StartsWith("CLAUDE_CODE_", StringComparison.Ordinal) && k != "CLAUDE_CODE_GIT_BASH_PATH"))
                .ToList();
            foreach (var k in inherited) psi.Environment.Remove(k);
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new UserFacingException($"Не удалось запустить Claude Code ({_executable}). Проверьте путь в настройках.", true, ex);
        }

        using var killOnCancel = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // процесс уже завершился
            }
        });

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.StandardInput.WriteLineAsync(BuildInputLine(system, user, png).AsMemory(), ct);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // CLI завершился раньше, чем прочитал ввод — причину покажем по stderr ниже.
        }

        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        ct.ThrowIfCancellationRequested();

        return ParseResult(stdout, stderr, process.ExitCode);
    }

    private static string BuildInputLine(string system, string user, byte[]? png)
    {
        var content = new JsonArray();
        if (png is not null)
        {
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = "image/png",
                    ["data"] = Convert.ToBase64String(png),
                },
            });
        }
        content.Add(new JsonObject { ["type"] = "text", ["text"] = system + "\n\n" + user });

        var line = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
        };
        return line.ToJsonString();
    }

    internal static string ParseResult(string stdout, string stderr, int exitCode)
    {
        string? resultText = null;
        bool isError = false;
        string? lastAssistantText = null;

        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            var type = node?["type"]?.GetValue<string>();
            if (type == "result")
            {
                resultText = node?["result"]?.GetValue<string>();
                isError = node?["is_error"]?.GetValue<bool>() == true || node?["subtype"]?.GetValue<string>() != "success";
            }
            else if (type == "assistant" && node?["message"]?["content"] is JsonArray blocks)
            {
                var sb = new StringBuilder();
                foreach (var b in blocks)
                    if (b?["type"]?.GetValue<string>() == "text") sb.Append(b["text"]?.GetValue<string>());
                if (sb.Length > 0) lastAssistantText = sb.ToString();
            }
        }

        var detail = Log.Redact(resultText ?? lastAssistantText ?? stderr.Trim());
        if (isError || (resultText is null && lastAssistantText is null))
        {
            Log.Warn($"Claude Code: exit={exitCode}, detail={Truncate(detail, 400)}");
            if (LooksLikeAuthProblem(detail))
                throw new UserFacingException(
                    "Claude Code не авторизован. Один раз выполните в терминале команду «claude auth login» и войдите в свой аккаунт Claude в открывшемся браузере, затем нажмите «Спросить ещё раз».", true);
            if (detail.Contains("limit", StringComparison.OrdinalIgnoreCase))
                throw new UserFacingException("Достигнут лимит подписки Claude. Подождите, пока он обновится. " + Truncate(detail, 200));
            throw new UserFacingException(string.IsNullOrWhiteSpace(detail)
                ? $"Claude Code завершился с ошибкой (код {exitCode})."
                : "Claude Code вернул ошибку: " + Truncate(detail, 300));
        }

        var text = (resultText ?? lastAssistantText ?? string.Empty).Trim();
        if (text.Length == 0) throw new UserFacingException("Claude вернул пустой ответ. Попробуйте ещё раз.");
        return text;
    }

    private static bool LooksLikeAuthProblem(string s) =>
        s.Contains("/login", StringComparison.OrdinalIgnoreCase)
        || s.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
        || s.Contains("Invalid API key", StringComparison.OrdinalIgnoreCase)
        || s.Contains("authentication", StringComparison.OrdinalIgnoreCase)
        || s.Contains("OAuth", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
