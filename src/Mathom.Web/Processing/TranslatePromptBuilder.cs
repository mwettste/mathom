using System.Collections.Generic;
using System.Text.Json;
using Mathom.Web.Languages;

namespace Mathom.Web.Processing;

public static class TranslatePromptBuilder
{
    public static string BuildSystemPrompt(string targetLocale, string styleHint, IReadOnlyList<string> glossaryTerms)
    {
        var name = Locales.DisplayName(targetLocale);
        var prompt =
            $"You translate a user's note into {name}. {styleHint}\n" +
            """
            Preserve meaning, tone, structure, and any line breaks. Produce natural, polished prose — not a literal word-for-word translation.
            Respond ONLY with a JSON object, no prose, with exactly these fields:
            {
              "title": string (the title in the target language, max ~8 words),
              "clean_text": string (the note in the target language)
            }
            """;
        if (glossaryTerms is { Count: > 0 })
            prompt += "\n\nKeep these domain terms unchanged (do not translate them): " + string.Join(", ", glossaryTerms) + ".";
        return prompt;
    }

    // The source note, given as JSON so the model clearly separates title from body.
    public static string BuildUserPrompt(string title, string text)
        => JsonSerializer.Serialize(new { title, clean_text = text });

    public static object ResponseSchema() => new
    {
        type = "object",
        properties = new
        {
            title = new { type = "string" },
            clean_text = new { type = "string" },
        },
        required = new[] { "title", "clean_text" },
        additionalProperties = false,
    };
}
