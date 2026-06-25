using System;
using System.Text.Json;

namespace Mathom.Web.Processing;

public static class TranslationResultParser
{
    public static TranslationResult Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new TranslationResult(GetRequiredString(root, "title"), GetRequiredString(root, "clean_text"));
        }
        catch (Exception ex) when (ex is not FormatException)
        {
            throw new FormatException("Malformed translation JSON.", ex);
        }
    }

    private static string GetRequiredString(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()))
            throw new FormatException($"Missing or empty field '{prop}'.");
        return v.GetString()!;
    }
}
