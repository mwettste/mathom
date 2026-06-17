using System;

namespace Mathom.Web.Domain;

public class ItemTag
{
    public Guid ItemId { get; set; }
    public Item Item { get; set; } = null!;
    public int TagId { get; set; }
    public Tag Tag { get; set; } = null!;
}
