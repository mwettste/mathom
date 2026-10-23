using System.Text.Json.Serialization;

namespace Mathom.Web.Api;

public record InboxItemDto(
    Guid Id, string Status, string SourceType, string? Title, string? Snippet, DateTimeOffset CreatedAt,
    ContextDto? SuggestedContext);

public record InboxPageDto(IReadOnlyList<InboxItemDto> Items, string? NextCursor);

// ContextId must be present: null moves the item to the Inbox, a missing property is a 400.
public record MoveRequest([property: JsonRequired] Guid? ContextId);

public record ContextDto(Guid Id, string Name);

public record TranslationDto(string Locale, string Title, string CleanText);

public record ItemDetailDto(
    Guid Id, string Status, string SourceType, string? Type, bool Actionable, string? Title, string? CleanText,
    string RawText, string? SourceLanguage, IReadOnlyList<string> Tags, IReadOnlyList<TranslationDto> Translations, string? Error,
    DateTimeOffset CreatedAt, DateTimeOffset? ProcessedAt, ContextDto? Context,
    ContextDto? SuggestedContext);

public static class ApiEnums
{
    // Wire format for every enum: its name, lowercased (pending, voice, idea, ...).
    public static string Lower(Enum e) => e.ToString().ToLowerInvariant();
}
