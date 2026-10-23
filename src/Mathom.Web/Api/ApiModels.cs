namespace Mathom.Web.Api;

public record InboxItemDto(
    Guid Id, string Status, string SourceType, string? Title, string? Snippet, DateTimeOffset CreatedAt);

public record InboxPageDto(IReadOnlyList<InboxItemDto> Items, string? NextCursor);

public static class ApiEnums
{
    // Wire format for every enum: its name, lowercased (pending, voice, idea, ...).
    public static string Lower(Enum e) => e.ToString().ToLowerInvariant();
}
