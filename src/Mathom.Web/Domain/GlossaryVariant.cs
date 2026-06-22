using System;

namespace Mathom.Web.Domain;

public class GlossaryVariant
{
    public Guid Id { get; set; }
    public Guid GlossaryTermId { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
