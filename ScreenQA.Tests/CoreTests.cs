using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ScreenQA.Native;
using ScreenQA.Services;
using ScreenQA.Services.Ai;
using Xunit;
using Drawing = System.Drawing;

namespace ScreenQA.Tests;

public class SecretTests
{
    [Fact]
    public void Dpapi_RoundTrip_AndCiphertextHidesKey()
    {
        const string key = "sk-ant-api03-TESTKEY-1234567890abcdef";
        var protectedValue = SecretProtector.Protect(key);

        Assert.DoesNotContain("TESTKEY", protectedValue);
        Assert.Equal(key, SecretProtector.Unprotect(protectedValue));
    }

    [Fact]
    public void SettingsJson_DoesNotContainPlainKey()
    {
        const string key = "sk-proj-PLAINKEY-abcdefghijklmnop";
        var settings = new AppSettings { OpenAiApiKeyProtected = SecretProtector.Protect(key) };
        var json = JsonSerializer.Serialize(settings);

        Assert.DoesNotContain("PLAINKEY", json);
    }

    [Fact]
    public void Unprotect_Garbage_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, SecretProtector.Unprotect("not-base64!!"));
    }
}

public class RedactionTests
{
    [Fact]
    public void Redact_RemovesRegisteredSecret()
    {
        Log.RegisterSecret("my-custom-secret-value");
        Assert.DoesNotContain("my-custom-secret-value", Log.Redact("ошибка: my-custom-secret-value отклонён"));
    }

    [Theory]
    [InlineData("Incorrect API key provided: sk-proj-abcdefghijklmnop1234", "abcdefghijklmnop1234")]
    [InlineData("x-api-key: sk-ant-api03-zzzzzzzzzzzzzzzzzz", "zzzzzzzzzzzzzzzzzz")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload", "eyJhbGciOiJIUzI1NiJ9")]
    public void Redact_RemovesKeyLikeStrings(string input, string secretPart)
    {
        var result = Log.Redact(input);
        Assert.DoesNotContain(secretPart, result);
        Assert.Contains("[скрыто]", result);
    }
}

public class PromptTests
{
    [Fact]
    public void Parse_ExtractsSections()
    {
        var a = StudyPrompt.Parse("ВОПРОС: Сколько будет 2+2?\nОТВЕТ: 4\nОБЪЯСНЕНИЕ: Сложение двух двоек даёт четыре.");
        Assert.Equal("Сколько будет 2+2?", a.Question);
        Assert.Equal("4", a.Answer);
        Assert.Equal("Сложение двух двоек даёт четыре.", a.Explanation);
    }

    [Fact]
    public void Parse_ToleratesMarkdownAndMultiline()
    {
        var a = StudyPrompt.Parse("**ВОПРОС:** Столица Франции?\n**ОТВЕТ:** B) Париж\n**ОБЪЯСНЕНИЕ:**\nПариж — столица.\nВторая строка.");
        Assert.Equal("B) Париж", a.Answer);
        Assert.Equal("Париж — столица.\nВторая строка.", a.Explanation);
    }

    [Fact]
    public void Parse_WithoutMarkers_ShowsWholeText()
    {
        var a = StudyPrompt.Parse("Просто ответ без формата");
        Assert.Equal("Просто ответ без формата", a.Answer);
    }

    [Fact]
    public void System_ContainsAnswerLanguage()
    {
        Assert.Contains("английский", StudyPrompt.BuildSystem("английский"));
    }
}

public class ProviderFactoryTests
{
    [Fact]
    public void Anthropic_WithoutKey_AsksToOpenSettings()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))) return;
        var ex = Assert.Throws<UserFacingException>(() => AiProviderFactory.Create(new AppSettings { Provider = ProviderIds.Anthropic }));
        Assert.True(ex.OfferSettings);
    }

    [Fact]
    public void ClaudeCode_IsDefault_AndBadPathIsReported()
    {
        Assert.Equal(ProviderIds.ClaudeCode, new AppSettings().Provider);
        var ex = Assert.Throws<UserFacingException>(() =>
            AiProviderFactory.Create(new AppSettings { ClaudeCodePath = @"C:\nope\claude.exe" }));
        Assert.True(ex.OfferSettings);
    }

    [Fact]
    public void OpenAi_RemoteHttp_IsRejected()
    {
        var s = new AppSettings
        {
            Provider = ProviderIds.OpenAiCompatible,
            OpenAiBaseUrl = "http://example.com/v1",
            OpenAiModel = "m",
            OpenAiApiKeyProtected = SecretProtector.Protect("sk-test-1234567890"),
        };
        Assert.Throws<UserFacingException>(() => AiProviderFactory.Create(s));
    }

    [Fact]
    public void OpenAi_LocalhostWithoutKey_IsAllowed()
    {
        var s = new AppSettings
        {
            Provider = ProviderIds.OpenAiCompatible,
            OpenAiBaseUrl = "http://localhost:11434/v1",
            OpenAiModel = "llama3",
        };
        Assert.IsType<OpenAiCompatibleProvider>(AiProviderFactory.Create(s));
    }

    [Fact]
    public void OpenAi_WithoutModel_IsRejected()
    {
        var s = new AppSettings { Provider = ProviderIds.OpenAiCompatible, OpenAiBaseUrl = "https://api.openai.com/v1" };
        Assert.Throws<UserFacingException>(() => AiProviderFactory.Create(s));
    }
}

public class ClaudeCodeOutputTests
{
    [Fact]
    public void Parses_SuccessResult()
    {
        var stdout =
            "{\"type\":\"system\",\"subtype\":\"init\"}\n" +
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"ОТВЕТ: 42\"}]}}\n" +
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"ВОПРОС: 12+30\\nОТВЕТ: 42\"}\n";
        Assert.Equal("ВОПРОС: 12+30\nОТВЕТ: 42", ClaudeCodeProvider.ParseResult(stdout, "", 0));
    }

    [Fact]
    public void AuthError_AsksToLogin()
    {
        var stdout = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"result\":\"Invalid API key · Please run /login\"}\n";
        var ex = Assert.Throws<UserFacingException>(() => ClaudeCodeProvider.ParseResult(stdout, "", 1));
        Assert.Contains("claude auth login", ex.Message);
        Assert.True(ex.OfferSettings);
    }

    [Fact]
    public void NoOutput_UsesStderr()
    {
        var ex = Assert.Throws<UserFacingException>(() => ClaudeCodeProvider.ParseResult("", "something broke", 2));
        Assert.Contains("something broke", ex.Message);
    }
}

public class HotkeySettingsTests
{
    [Fact]
    public void Default_IsSingleQ()
    {
        Assert.Equal(new[] { new HotkeyBinding(0, 0x51) }, new AppSettings().GetHotkeyBindings());
    }

    [Fact]
    public void Bindings_SkipEmptyAndDuplicates_AndCloneIsDeep()
    {
        var s = new AppSettings();
        s.Hotkeys.Add(new HotkeySetting { Modifiers = NativeMethods.MOD_SHIFT, Key = 0x51 });
        s.Hotkeys.Add(new HotkeySetting { Modifiers = 0, Key = 0x51 });
        s.Hotkeys.Add(new HotkeySetting { Modifiers = 0, Key = 0 });

        Assert.Equal(new[] { new HotkeyBinding(0, 0x51), new HotkeyBinding(NativeMethods.MOD_SHIFT, 0x51) }, s.GetHotkeyBindings());

        var copy = s.Clone();
        copy.Hotkeys.Clear();
        Assert.Equal(4, s.Hotkeys.Count);
    }

    [Fact]
    public void Settings_RoundTripThroughJson()
    {
        var s = new AppSettings();
        s.Hotkeys.Add(new HotkeySetting { Modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, Key = 0x31 });
        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s))!;
        Assert.Equal(s.GetHotkeyBindings(), back.GetHotkeyBindings());
    }
}

public class HotkeyFormatterTests
{
    [Fact]
    public void Formats_DefaultAndCombo()
    {
        Assert.Equal("Q", HotkeyFormatter.Format(0, 0x51));
        Assert.Equal("Ctrl + Shift + Q", HotkeyFormatter.Format(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, 0x51));
        Assert.Equal("Alt + 1", HotkeyFormatter.Format(NativeMethods.MOD_ALT, 0x31));
    }
}

public class OcrTests
{
    [Fact]
    public async Task Recognizes_RenderedText()
    {
        var languages = OcrService.GetAvailableLanguages();
        Assert.NotEmpty(languages); // на машине должен быть хотя бы один язык OCR

        var tag = languages.Any(l => l.Tag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            ? languages.First(l => l.Tag.StartsWith("en", StringComparison.OrdinalIgnoreCase)).Tag
            : string.Empty;

        using var bmp = new Drawing.Bitmap(520, 70);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(Drawing.Color.White);
            g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new Drawing.Font("Segoe UI", 22);
            g.DrawString("What is 12 plus 30?", font, Drawing.Brushes.Black, 10, 12);
        }

        var text = await OcrService.RecognizeAsync(bmp, tag, CancellationToken.None);
        Assert.Contains("12", text);
        Assert.Contains("30", text);
    }

    [Fact]
    public async Task MissingLanguage_GivesFriendlyError()
    {
        using var bmp = new Drawing.Bitmap(10, 10);
        var ex = await Assert.ThrowsAsync<UserFacingException>(() => OcrService.RecognizeAsync(bmp, "zu-ZA", CancellationToken.None));
        Assert.True(ex.OfferSettings);
    }
}
