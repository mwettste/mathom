using System;
using System.Collections.Generic;
using System.Text.Json;
using Mathom.Web.Domain;

namespace Mathom.Web.Processing;

public static class CleanupResultParser
{
    public static CleanupResult Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var title = GetRequiredString(root, "title");
            var cleanText = GetRequiredString(root, "clean_text");
            var itemType = ParseEnum<ItemType>(GetRequiredString(root, "item_type"));
            bool actionable = false;
            if (root.TryGetProperty("actionable", out var a))
            {
                if (a.ValueKind == JsonValueKind.True)
                    actionable = true;
                else if (a.ValueKind == JsonValueKind.False)
                    actionable = false;
                else
                    throw new FormatException("Field 'actionable' must be a boolean.");
            }

            var tags = new List<CleanupTag>();
            if (root.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tagsEl.EnumerateArray())
                {
                    var name = GetRequiredString(t, "name");
                    var kind = ParseEnum<TagKind>(GetRequiredString(t, "kind"));
                    tags.Add(new CleanupTag(name, kind));
                }
            }

            return new CleanupResult(title, cleanText, itemType, actionable, tags);
        }
        catch (Exception ex) when (ex is not FormatException)
        {
            throw new FormatException("Malformed cleanup JSON.", ex);
        }
    }

    private static string GetRequiredString(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()))
            throw new FormatException($"Missing or empty field '{prop}'.");
        return v.GetString()!;
    }

    private static TEnum ParseEnum<TEnum>(string value) where TEnum : struct
    {
        if (!Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed))
            throw new FormatException($"Unknown enum value '{value}' for {typeof(TEnum).Name}.");
        return parsed;
    }
}
