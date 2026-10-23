namespace Mathom.Web.Api;

public record InboxItemDto(
    Guid Id, string Status, string SourceType, string? Title, string? Snippet, DateTimeOffset CreatedAt);

public record InboxPageDto(IReadOnlyList<InboxItemDto> Items, string? NextCursor);

public record ContextDto(Guid Id, string Name);

public record TranslationDto(string Locale, string Title, string CleanText);

public record ItemDetailDto(
    Guid Id, string Status, string SourceType, string? Type, bool Actionable, string? Title, string? CleanText,
    string? SourceLanguage, IReadOnlyList<string> Tags, IReadOnlyList<TranslationDto> Translations, string? Error,
    DateTimeOffset CreatedAt, DateTimeOffset? ProcessedAt, ContextDto? Context);

public static class ApiEnums
{
    // Wire format for every enum: its name, lowercased (pending, voice, idea, ...).
    public static string Lower(Enum e) => e.ToString().ToLowerInvariant();
}
