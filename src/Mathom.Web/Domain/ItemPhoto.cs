using System;

namespace Mathom.Web.Domain;

public class ItemPhoto
{
    public Guid Id { get; set; }
    public Guid ItemId { get; set; }
    public Item Item { get; set; } = null!;

    // Opaque IMediaStore key (e.g. "a1b2…f6.jpg"); the extension drives the served content type.
    public string MediaPath { get; set; } = string.Empty;

    // 0-based display / read order within the item.
    public int Order { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
