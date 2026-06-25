// src/Mathom.Web/Domain/ItemTranslation.cs
using System;

namespace Mathom.Web.Domain;

// A polished variant of an Item in one non-source active language.
public class ItemTranslation
{
    public Guid Id { get; set; }
    public Guid ItemId { get; set; }
    public Item Item { get; set; } = null!;
    public string Locale { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string CleanText { get; set; } = string.Empty;

    // Postgres-generated full-text search vector (configured in MathomDbContext).
    public NpgsqlTypes.NpgsqlTsVector? SearchVector { get; set; }
}
