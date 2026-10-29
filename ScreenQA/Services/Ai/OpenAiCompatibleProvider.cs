using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenQA.Services.Ai;

/// <summary>
/// Любой сервис с API Chat Completions в формате OpenAI: OpenAI, OpenRouter, Groq,
/// а также локальные Ollama / LM Studio (http://localhost:…/v1, ключ не нужен).
/// </summary>
internal sealed class OpenAiCompatibleProvider : IAiProvider
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly Uri _baseUrl;
    private readonly string _model;
    private readonly string _apiKey;

    public OpenAiCompatibleProvider(Uri baseUrl, string model, string apiKey)
    {
        _baseUrl = baseUrl;
        _model = model;
        _apiKey = apiKey;
    }

    public string DisplayName => $"{_baseUrl.Host} ({_model})";

    public async Task<string> CompleteAsync(string system, string user, byte[]? png, CancellationToken ct)
    {
        JsonNode userContent = png is null
            ? JsonValue.Create(user)!
            : new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = user },
                new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(png) },
                },
            };

        var body = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = userContent },
            },
        };

        var endpoint = new Uri(_baseUrl.AbsoluteUri.TrimEnd('/') + "/chat/completions");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new UserFacingException($"Нет соединения с {_baseUrl.Host}. Проверьте адрес API и интернет.", true, ex);
        }

        using (response)
        {
            var json = await response.Content.ReadAsStringAsync(ct);
            int status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var detail = Log.Redact(ExtractError(json));
                Log.Warn($"OpenAI-compatible HTTP {status}: {detail}");
                throw status switch
                {
                    401 => new UserFacingException("Ключ API не принят (401). Проверьте ключ в настройках.", true),
                    403 => new UserFacingException("Доступ запрещён (403): " + detail, true),
                    404 => new UserFacingException($"Адрес или модель «{_model}» не найдены (404). Проверьте настройки.", true),
                    429 => new UserFacingException("Превышен лимит запросов или закончился баланс (429). " + detail),
                    >= 500 => new UserFacingException($"Сервис {_baseUrl.Host} временно недоступен ({status})."),
                    _ => new UserFacingException($"Ошибка API ({status}): {detail}", true),
                };
            }

            return ExtractText(json);
        }
    }

    private static string ExtractText(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            var message = root?["choices"]?[0]?["message"];
            var content = message?["content"];
            string text = content switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonArray parts => string.Join("\n", parts.Select(p => p?["text"]?.GetValue<string>())),
                _ => string.Empty,
            };
            if (string.IsNullOrWhiteSpace(text))
            {
                var refusal = message?["refusal"]?.GetValue<string>();
                throw new UserFacingException(string.IsNullOrEmpty(refusal) ? "Модель вернула пустой ответ." : "Модель отказалась отвечать: " + refusal);
            }
            return text.Trim();
        }
        catch (JsonException ex)
        {
            throw new UserFacingException("Сервис вернул ответ в неожиданном формате. Проверьте, что адрес API совместим с OpenAI Chat Completions.", true, ex);
        }
    }

    private static string ExtractError(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            var msg = root?["error"]?["message"]?.GetValue<string>() ?? root?["error"]?.ToString();
            if (!string.IsNullOrWhiteSpace(msg)) return msg.Length > 300 ? msg[..300] + "…" : msg;
        }
        catch (Exception)
        {
            // не JSON — ниже вернём начало тела
        }
        json = json.Trim();
        return json.Length > 200 ? json[..200] + "…" : json;
    }
}
