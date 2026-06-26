using System.Collections.Generic;
using System.Linq;

namespace Mathom.Web.Languages;

/// <param name="Code">Catalog code, e.g. "en", "de-DE", "de-CH".</param>
/// <param name="BaseLanguage">ISO 639-1 base, e.g. "de" — used to map auto-detection to a locale.</param>
/// <param name="StyleHint">LLM instruction for cultural rendering of this locale.</param>
/// <param name="PgConfig">Postgres full-text config. "simple" for all entries in v1.</param>
public record Locale(string Code, string DisplayName, string BaseLanguage, string StyleHint, string PgConfig);

public static class Locales
{
    public static readonly IReadOnlyList<Locale> All = new[]
    {
        new Locale("en",    "English",              "en", "Standard English.", "simple"),
        new Locale("de-DE", "German (Germany)",     "de", "Standard German as written in Germany (uses ß where appropriate).", "simple"),
        new Locale("de-CH", "German (Switzerland)", "de", "Swiss Standard German: always use 'ss' instead of 'ß'; prefer Swiss vocabulary and spelling.", "simple"),
        new Locale("fr-FR", "French",               "fr", "Standard French.", "simple"),
        new Locale("es-ES", "Spanish",              "es", "Standard European Spanish.", "simple"),
        new Locale("it-IT", "Italian",              "it", "Standard Italian.", "simple"),
    };

    public static bool IsSupported(string code) => All.Any(l => l.Code == code);
    public static Locale? Find(string code) => All.FirstOrDefault(l => l.Code == code);
    public static string DisplayName(string code) => Find(code)?.DisplayName ?? code;
    public static string StyleHint(string code) => Find(code)?.StyleHint ?? string.Empty;

    /// <summary>
    /// Map a detected base language to a concrete source locale: prefer the user's
    /// primary if its base matches; else the first active locale of that base; else the
    /// catalog's default locale for that base; else the primary.
    /// </summary>
    public static string ResolveSourceLocale(string? detectedBase, string primaryLocale, IReadOnlyList<string> activeLocales)
    {
        var b = (detectedBase ?? string.Empty).Trim().ToLowerInvariant();
        if (b.Length >= 2) b = b.Substring(0, 2);
        if (b.Length == 0) return primaryLocale;

        if (Find(primaryLocale)?.BaseLanguage == b) return primaryLocale;

        var active = activeLocales.FirstOrDefault(c => Find(c)?.BaseLanguage == b);
        if (active is not null) return active;

        var catalogDefault = All.FirstOrDefault(l => l.BaseLanguage == b);
        return catalogDefault?.Code ?? primaryLocale;
    }
}
