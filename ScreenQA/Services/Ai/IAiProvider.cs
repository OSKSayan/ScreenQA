using System;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenQA.Services.Ai;

internal interface IAiProvider
{
    string DisplayName { get; }

    /// <summary>Возвращает текст ответа модели. Ошибки — <see cref="UserFacingException"/> с понятным текстом.</summary>
    Task<string> CompleteAsync(string system, string user, byte[]? png, CancellationToken ct);
}

internal static class AiProviderFactory
{
    public static IAiProvider Create(AppSettings settings)
    {
        switch (settings.Provider)
        {
            case ProviderIds.ClaudeCode:
            {
                var exe = ClaudeCodeProvider.FindExecutable(settings.ClaudeCodePath)
                          ?? throw new UserFacingException(
                              string.IsNullOrWhiteSpace(settings.ClaudeCodePath)
                                  ? "Claude Code не найден. Установите его (claude.com/claude-code) или укажите путь к claude.exe в настройках."
                                  : $"Файл «{settings.ClaudeCodePath}» не найден. Проверьте путь к claude.exe в настройках.",
                              true);
                var model = string.IsNullOrWhiteSpace(settings.ClaudeCodeModel) ? ClaudeCodeDefaults.Model : settings.ClaudeCodeModel.Trim();
                var effort = Array.IndexOf(ClaudeCodeDefaults.Efforts, settings.ClaudeCodeEffort) >= 0
                    ? settings.ClaudeCodeEffort
                    : ClaudeCodeDefaults.Effort;
                return new ClaudeCodeProvider(exe, model, effort);
            }

            case ProviderIds.OpenAiCompatible:
            {
                var baseUrl = (settings.OpenAiBaseUrl ?? string.Empty).Trim();
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                    throw new UserFacingException("Укажите корректный адрес API (например, https://api.openai.com/v1).", true);
                if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
                    throw new UserFacingException("Для удалённого сервера нужен адрес https:// — иначе ключ уйдёт по сети открытым текстом. http:// разрешён только для localhost.", true);
                if (string.IsNullOrWhiteSpace(settings.OpenAiModel))
                    throw new UserFacingException("Не указана модель для OpenAI-совместимого провайдера. Откройте настройки.", true);

                var key = SecretProtector.Unprotect(settings.OpenAiApiKeyProtected);
                if (string.IsNullOrEmpty(key) && !uri.IsLoopback)
                    throw new UserFacingException("Не задан ключ API для OpenAI-совместимого провайдера. Откройте настройки и вставьте ключ.", true);
                Log.RegisterSecret(key);
                return new OpenAiCompatibleProvider(uri, settings.OpenAiModel.Trim(), key);
            }

            default:
            {
                var key = SecretProtector.Unprotect(settings.AnthropicApiKeyProtected);
                if (string.IsNullOrEmpty(key)) key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? string.Empty;
                if (string.IsNullOrEmpty(key))
                    throw new UserFacingException("Не задан ключ API Anthropic. Откройте настройки и вставьте ключ (получить его можно в console.anthropic.com).", true);
                if (string.IsNullOrWhiteSpace(settings.AnthropicModel))
                    throw new UserFacingException("Не указана модель Claude. Откройте настройки.", true);
                Log.RegisterSecret(key);
                return new AnthropicProvider(key, settings.AnthropicModel.Trim());
            }
        }
    }
}
