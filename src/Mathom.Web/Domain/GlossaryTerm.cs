using System;

namespace Mathom.Web.Domain;

public class GlossaryTerm
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Term { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
