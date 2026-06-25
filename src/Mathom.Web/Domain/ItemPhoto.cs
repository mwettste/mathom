using System;
using NanoidDotNet;

namespace Mathom.Web.Domain;

public class ItemPhoto
{
    public Guid Id { get; set; }
    public Guid ItemId { get; set; }
    public Item Item { get; set; } = null!;

    // Opaque, unguessable public id used in /media URLs; decoupled from Id and the storage key.
    // Assigned once at creation and never changed (the photo bytes are immutable).
    public string ExternalId { get; set; } = Nanoid.Generate();

    // Opaque IMediaStore key of the stored ORIGINAL (e.g. "a1b2…f6.jpg").
    public string MediaPath { get; set; } = string.Empty;

    // Opaque IMediaStore key of the ≤1600px JPEG display/OCR variant; null until generated.
    public string? DisplayPath { get; set; }

    // 0-based display / read order within the item.
    public int Order { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
