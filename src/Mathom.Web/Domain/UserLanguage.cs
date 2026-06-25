using System;

namespace Mathom.Web.Domain;

// A working language for a user. Exactly one row per user has IsPrimary = true
// (enforced in UserLanguageService). Locale is a Locales catalog code.
public class UserLanguage
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Locale { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
