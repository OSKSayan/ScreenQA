using System;
using System.Text.RegularExpressions;

namespace ScreenQA.Services.Ai;

internal sealed record AiAnswer(string Question, string Answer, string Explanation);

/// <summary>Промпт для разбора учебного вопроса и разбор ответа модели.</summary>
internal static class StudyPrompt
{
    private static readonly Regex Sections = new(
        @"^[\s*#_]*(?<name>ВОПРОС|ОТВЕТ|ОБЪЯСНЕНИЕ)[\s*_]*:[\s*_]*",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string BuildSystem(string answerLanguage)
    {
        var lang = string.IsNullOrWhiteSpace(answerLanguage) ? "русский" : answerLanguage.Trim();
        return
            "Ты — внимательный репетитор. Пользователь выделил на экране фрагмент с учебным вопросом.\n" +
            "Текст получен оптическим распознаванием и может содержать ошибки (перепутанные буквы, лишние символы, " +
            "разорванные строки) — восстанавливай смысл. Если приложено изображение, опирайся на него в первую очередь.\n" +
            "Правила:\n" +
            "- Сначала точно прочитай условие: сверь распознанный текст с изображением — цифры, знаки, степени, " +
            "единицы измерения, отрицания («не», «кроме», «неверно»). Ошибка OCR в одном символе меняет ответ.\n" +
            "- Реши задачу по шагам, затем независимо перепроверь результат (подставь ответ, пересчитай другим способом, " +
            "проверь каждый вариант). Если проверка не сошлась — найди ошибку и исправь до того, как отвечать.\n" +
            "- Если есть варианты ответа, укажи правильный вариант (букву или номер) и его текст. " +
            "Если подходят несколько или ни один — так и скажи.\n" +
            "- Если вопросов несколько, разбери каждый по порядку.\n" +
            "- Если вопроса нет или текст нечитаем, честно скажи об этом и предложи выделить область заново.\n" +
            "- Не выдумывай факты. Если не уверен или условие допускает разные толкования, прямо напиши об этом " +
            "в объяснении и укажи, от чего зависит ответ.\n" +
            $"Пиши на языке: {lang}. Метки разделов оставляй ровно такими, как ниже, без Markdown-заголовков.\n" +
            "Формат ответа:\n" +
            "ВОПРОС: <вопрос своими словами, коротко>\n" +
            "ОТВЕТ: <короткий итоговый ответ>\n" +
            "ОБЪЯСНЕНИЕ: <краткое объяснение решения, 2–6 предложений>";
    }

    public static string BuildUser(string recognizedText, bool hasImage)
    {
        var text = string.IsNullOrWhiteSpace(recognizedText)
            ? "(текст распознать не удалось)"
            : recognizedText.Trim();
        return (hasImage ? "Изображение выделенной области приложено.\n" : string.Empty) +
               "Распознанный текст:\n\"\"\"\n" + text + "\n\"\"\"";
    }

    public static AiAnswer Parse(string raw)
    {
        raw = (raw ?? string.Empty).Trim();
        string question = string.Empty, answer = string.Empty, explanation = string.Empty;

        var matches = Sections.Matches(raw);
        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            int start = m.Index + m.Length;
            int end = i + 1 < matches.Count ? matches[i + 1].Index : raw.Length;
            var body = raw[start..end].Trim();
            switch (m.Groups["name"].Value.ToUpperInvariant())
            {
                case "ВОПРОС": question = body; break;
                case "ОТВЕТ": answer = body; break;
                case "ОБЪЯСНЕНИЕ": explanation = body; break;
            }
        }

        // Модель не соблюла формат — показываем ответ целиком.
        if (string.IsNullOrEmpty(answer)) return new AiAnswer(question, raw, explanation);
        return new AiAnswer(question, answer, explanation);
    }
}
