using System;
using System.Collections.Generic;

namespace Mathom.Web.Domain;

public class GlossaryTerm
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    // Null = Inbox glossary. FK to Context with ON DELETE CASCADE.
    public Guid? ContextId { get; set; }
    public string Term { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public List<GlossaryVariant> Variants { get; set; } = new();
    public string? Description { get; set; }
}
