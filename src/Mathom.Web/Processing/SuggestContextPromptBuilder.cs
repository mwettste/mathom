using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Mathom.Web.Processing;

public static class SuggestContextPromptBuilder
{
    private const int MaxTitleChars = 120;
    private const int MaxTextChars = 4000;

    public static string BuildSystemPrompt() =>
        """
        The user files their notes into personal contexts (folders). Pick the single context the given note most likely belongs to.
        Use each context's name, its domain terms (glossary_terms), and the titles of notes already filed there (recent_titles).
        Names, terms and titles may be in German, English or other languages.
        Answer "none" when no context is a reasonably clear fit.
        Respond ONLY with a JSON object, no prose, with exactly this field:
        {
          "choice": string (the "key" of the chosen context, or "none")
        }
        """;

    public static string BuildUserPrompt(string title, string text, IReadOnlyList<ContextCandidate> candidates)
        => JsonSerializer.Serialize(new
        {
            note = new { title = Truncate(title, MaxTitleChars), text = Truncate(text, MaxTextChars) },
            contexts = candidates.Select(c => new
            {
                key = c.Key,
                name = c.Name,
                glossary_terms = c.GlossaryTerms,
                recent_titles = c.RecentTitles.Select(t => Truncate(t, MaxTitleChars)).ToArray(),
            }).ToArray(),
        });

    public static object ResponseSchema(IReadOnlyList<string> keys) => new
    {
        type = "object",
        properties = new
        {
            choice = new { type = "string", @enum = keys.Append("none").ToArray() },
        },
        required = new[] { "choice" },
        additionalProperties = false,
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
