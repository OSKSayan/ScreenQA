using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace ScreenQA.Services.Ai;

/// <summary>Claude через официальный SDK Anthropic для .NET.</summary>
internal sealed class AnthropicProvider : IAiProvider
{
    // Модели, для которых включаем серверный резерв: если классификатор безопасности
    // ошибочно отклонит запрос, его в том же вызове обслужит запасная модель.
    private static readonly HashSet<string> FallbackModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "claude-opus-5-5", "claude-opus-5", "claude-fable-5-1", "claude-sonnet-5-5",
    };

    private readonly string _apiKey;
    private readonly string _model;

    public AnthropicProvider(string apiKey, string model)
    {
        _apiKey = apiKey;
        _model = model;
    }

    public string DisplayName => $"Claude ({_model})";

    public async Task<string> CompleteAsync(string system, string user, byte[]? png, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = _apiKey };

        var content = new List<BetaContentBlockParam>();
        if (png is not null)
        {
            content.Add(new BetaImageBlockParam
            {
                Source = new BetaBase64ImageSource
                {
                    Data = Convert.ToBase64String(png),
                    MediaType = MediaType.ImagePng,
                },
            });
        }
        content.Add(new BetaTextBlockParam { Text = user });

        bool useFallback = FallbackModels.Contains(_model);
        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = 16000,
            System = system,
            Messages = [new BetaUserMessageParam { Content = content }],
        };
        if (useFallback)
        {
            // Режим "default": сервер сам выбирает запасную модель по категории отказа.
            parameters = parameters with
            {
                Betas = ["server-side-fallback-2026-07-01"],
                Fallbacks = new Default(),
            };
        }

        BetaMessage response;
        try
        {
            response = await client.Beta.Messages.Create(parameters, ct);
        }
        catch (AnthropicUnauthorizedException ex)
        {
            throw new UserFacingException("Ключ API Anthropic не принят (401). Проверьте ключ в настройках.", true, ex);
        }
        catch (AnthropicForbiddenException ex)
        {
            throw new UserFacingException("Доступ запрещён (403): у ключа нет прав на эту модель или функцию.", true, ex);
        }
        catch (AnthropicNotFoundException ex)
        {
            throw new UserFacingException($"Модель «{_model}» не найдена. Проверьте название модели в настройках.", true, ex);
        }
        catch (AnthropicRateLimitException ex)
        {
            throw new UserFacingException("Превышен лимит запросов Anthropic (429). Подождите немного и попробуйте снова.", false, ex);
        }
        catch (Anthropic5xxException ex)
        {
            throw new UserFacingException("Сервис Anthropic временно недоступен. Попробуйте ещё раз через минуту.", false, ex);
        }
        catch (AnthropicBadRequestException ex)
        {
            Log.Error("Anthropic 400", ex);
            throw new UserFacingException("Запрос отклонён сервисом Anthropic: " + Log.Redact(ex.Message), true, ex);
        }
        catch (AnthropicIOException ex)
        {
            throw new UserFacingException("Нет соединения с api.anthropic.com. Проверьте интернет или прокси.", false, ex);
        }
        catch (AnthropicApiException ex)
        {
            Log.Error("Anthropic API", ex);
            throw new UserFacingException("Ошибка Anthropic API: " + Log.Redact(ex.Message), false, ex);
        }

        if (response.StopReason == "refusal")
        {
            throw new UserFacingException("Модель отказалась отвечать на этот запрос. Попробуйте переформулировать или выделить другой фрагмент.");
        }

        var text = string.Join("\n", response.Content
            .Select(b => b.Value)
            .OfType<BetaTextBlock>()
            .Select(t => t.Text)).Trim();

        if (string.IsNullOrEmpty(text))
            throw new UserFacingException("Модель вернула пустой ответ. Попробуйте ещё раз.");

        if (response.StopReason == "max_tokens")
            text += "\n\n(ответ обрезан по лимиту длины)";

        return text;
    }
}
