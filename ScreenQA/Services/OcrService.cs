using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using Drawing = System.Drawing;

namespace ScreenQA.Services;

internal sealed record OcrLanguageInfo(string Tag, string DisplayName);

/// <summary>
/// Локальное распознавание текста встроенным движком Windows (Windows.Media.Ocr).
/// Интернет и ключи не нужны, но язык должен быть установлен в Windows с компонентом OCR.
/// </summary>
internal static class OcrService
{
    public const string InstallHint =
        "Добавьте язык: Параметры → Время и язык → Язык и регион → Добавить язык " +
        "(отметьте «Оптическое распознавание символов»), затем перезапустите ScreenQA.";

    public static IReadOnlyList<OcrLanguageInfo> GetAvailableLanguages()
    {
        try
        {
            return OcrEngine.AvailableRecognizerLanguages
                .Select(l => new OcrLanguageInfo(l.LanguageTag, $"{l.DisplayName} ({l.LanguageTag})"))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось получить список языков OCR", ex);
            return Array.Empty<OcrLanguageInfo>();
        }
    }

    /// <summary>
    /// Подготовка изображения выполняется синхронно до первого await,
    /// поэтому исходный Bitmap можно освободить сразу после вызова.
    /// </summary>
    public static Task<string> RecognizeAsync(Drawing.Bitmap source, string languageTag, CancellationToken ct)
    {
        var engine = CreateEngine(languageTag);
        byte[] png;
        using (var prepared = Prepare(source)) png = ImageUtil.ToPng(prepared);
        return RecognizePngAsync(engine, png, ct);
    }

    private static OcrEngine CreateEngine(string languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag))
        {
            return OcrEngine.TryCreateFromUserProfileLanguages()
                   ?? throw new UserFacingException(
                       "В Windows не установлено ни одного языка с поддержкой распознавания текста. " + InstallHint, true);
        }

        Language language;
        try
        {
            language = new Language(languageTag);
        }
        catch (Exception ex)
        {
            throw new UserFacingException($"Некорректный язык распознавания «{languageTag}».", true, ex);
        }

        if (!OcrEngine.IsLanguageSupported(language))
        {
            throw new UserFacingException(
                $"Язык распознавания «{language.DisplayName}» не установлен в Windows. " + InstallHint, true);
        }

        return OcrEngine.TryCreateFromLanguage(language)
               ?? throw new UserFacingException($"Не удалось запустить распознавание для языка «{language.DisplayName}».", true);
    }

    /// <summary>Мелкий текст увеличиваем (OCR Windows плохо читает строки ниже ~20 px), слишком большие картинки уменьшаем.</summary>
    private static Drawing.Bitmap Prepare(Drawing.Bitmap source)
    {
        double maxDim = OcrEngine.MaxImageDimension;
        double longSide = Math.Max(source.Width, source.Height);
        double scale = 1.0;
        if (longSide < 1200) scale = Math.Min(2.5, 1200 / longSide);
        if (longSide * scale > maxDim) scale = maxDim / longSide;
        return ImageUtil.Scale(source, scale);
    }

    private static async Task<string> RecognizePngAsync(OcrEngine engine, byte[] png, CancellationToken ct)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        ct.ThrowIfCancellationRequested();

        var result = await engine.RecognizeAsync(bitmap);
        ct.ThrowIfCancellationRequested();

        return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text)).Trim();
    }
}
