using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Mathom.Web.Processing;

public static class SuggestContextResultParser
{
    /// <summary>
    /// Returns the chosen candidate key, or null for "none", an unknown value or a missing field.
    /// Throws <see cref="FormatException"/> only when the payload is not JSON.
    /// </summary>
    public static string? Parse(string json, IReadOnlyCollection<string> keys)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("choice", out var v)
                || v.ValueKind != JsonValueKind.String)
                return null;
            var choice = v.GetString();
            return choice is not null && keys.Contains(choice) ? choice : null;
        }
        catch (JsonException ex)
        {
            throw new FormatException("Malformed context suggestion JSON.", ex);
        }
    }
}
