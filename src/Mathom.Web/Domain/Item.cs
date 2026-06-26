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

    // Optional user-typed context captured alongside photos. Steers vision extraction and is
    // preserved into RawText during processing. Distinct from ContextId (the grouping FK).
    public string? CaptureNote { get; set; }

    public string? CleanText { get; set; }
    public string? Title { get; set; }
    public ItemType? ItemType { get; set; }
    public bool Actionable { get; set; }
    public string? MediaPath { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? Error { get; set; }

    // Null = live; set = soft-deleted (in trash). Excluded from all normal queries
    // by a global query filter in MathomDbContext.
    public DateTimeOffset? DeletedAt { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    // Null = Inbox (no context). FK to Context with ON DELETE SET NULL.
    public Guid? ContextId { get; set; }
    public List<ItemTag> ItemTags { get; set; } = new();
    public List<ItemPhoto> Photos { get; set; } = new();

    // Postgres-generated full-text search vector (configured in MathomDbContext).
    public NpgsqlTypes.NpgsqlTsVector? SearchVector { get; set; }

    // Detected source locale (Locales code), e.g. "de-CH". Null until processed.
    public string? SourceLanguage { get; set; }

    // Polished variants in the user's other active languages (source lives on this row).
    public List<ItemTranslation> Translations { get; set; } = new();

    // Multilingual semantic-search vector over Title + CleanText (source language).
    // Null until embedded (pipeline is best-effort; the backfill fills gaps).
    public Pgvector.Vector? Embedding { get; set; }

    // Model that produced the current Embedding; lets the backfill detect stale vectors.
    public string? EmbeddingModel { get; set; }
    public DateTimeOffset? EmbeddedAt { get; set; }

    public static Item CreatePending(SourceType sourceType, string rawText, string idempotencyKey, string userId, DateTimeOffset now)
        => new()
        {
            Id = Guid.NewGuid(),
            Status = ItemStatus.Pending,
            SourceType = sourceType,
            RawText = rawText,
            IdempotencyKey = idempotencyKey,
            UserId = userId,
            CreatedAt = now,
        };
}
