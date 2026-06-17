using System;
using System.Collections.Generic;

namespace Mathom.Web.Domain;

public enum ItemStatus { Pending, Processing, Ready, Failed }
public enum SourceType { Text, Voice, Photo }
public enum ItemType { Idea, Task, Note, Reference, Journal }

public class Item
{
    public Guid Id { get; set; }
    public ItemStatus Status { get; set; }
    public SourceType SourceType { get; set; }
    public string RawText { get; set; } = string.Empty;
    public string? CleanText { get; set; }
    public string? Title { get; set; }
    public ItemType? ItemType { get; set; }
    public bool Actionable { get; set; }
    public string? MediaPath { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? Error { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public List<ItemTag> ItemTags { get; set; } = new();

    // Postgres-generated full-text search vector (configured in MathomDbContext).
    public NpgsqlTypes.NpgsqlTsVector? SearchVector { get; set; }

    public static Item CreatePending(SourceType sourceType, string rawText, string idempotencyKey, DateTimeOffset now)
        => new()
        {
            Id = Guid.NewGuid(),
            Status = ItemStatus.Pending,
            SourceType = sourceType,
            RawText = rawText,
            IdempotencyKey = idempotencyKey,
            CreatedAt = now,
        };
}
