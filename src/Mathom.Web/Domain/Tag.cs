using System.Collections.Generic;

namespace Mathom.Web.Domain;

public enum TagKind { Topic, Person, Project, Entity }

public class Tag
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public TagKind Kind { get; set; }
    public List<ItemTag> ItemTags { get; set; } = new();
}
