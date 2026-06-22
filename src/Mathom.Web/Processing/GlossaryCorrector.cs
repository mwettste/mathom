using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Mathom.Web.Processing;

// Deterministic, whole-word, case-insensitive replacement of known mis-heard
// variants with their canonical glossary term, applied to the cleanup result's
// title, clean text, and tag names. Pure; never touches the transcript.
public static class GlossaryCorrector
{
    public static CleanupResult Apply(CleanupResult result, IReadOnlyDictionary<string, string> variantToTerm)
    {
        if (variantToTerm is null || variantToTerm.Count == 0) return result;

        string Fix(string? s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            foreach (var kv in variantToTerm)
                s = Regex.Replace(s, @"\b" + Regex.Escape(kv.Key) + @"\b", kv.Value, RegexOptions.IgnoreCase);
            return s;
        }

        return result with
        {
            Title = Fix(result.Title),
            CleanText = Fix(result.CleanText),
            Tags = result.Tags.Select(t => t with { Name = Fix(t.Name) }).ToList(),
        };
    }
}
