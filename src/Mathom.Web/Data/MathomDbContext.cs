using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Mathom.Web.Domain;

namespace Mathom.Web.Data;

public class MathomDbContext : IdentityDbContext<ApplicationUser>
{
    public MathomDbContext(DbContextOptions<MathomDbContext> options) : base(options) { }

    public DbSet<Item> Items => Set<Item>()!;
    public DbSet<Tag> Tags => Set<Tag>()!;
    public DbSet<ItemTag> ItemTags => Set<ItemTag>()!;

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Item>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.RawText).IsRequired();
            e.Property(x => x.IdempotencyKey).IsRequired();
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.CreatedAt);

            // Generated tsvector over Title + CleanText for full-text search.
#pragma warning disable CS8603
            e.HasGeneratedTsVectorColumn(
                    x => x.SearchVector,
                    "english",
                    x => new { x.Title, x.CleanText })
                .HasIndex(x => x.SearchVector)
                .HasMethod("GIN");
#pragma warning restore CS8603
        });

        b.Entity<Tag>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired();
            e.HasIndex(x => new { x.Name, x.Kind }).IsUnique();
        });

        b.Entity<ItemTag>(e =>
        {
            e.HasKey(x => new { x.ItemId, x.TagId });
            e.HasOne(x => x.Item).WithMany(i => i.ItemTags).HasForeignKey(x => x.ItemId);
            e.HasOne(x => x.Tag).WithMany(t => t.ItemTags).HasForeignKey(x => x.TagId);
        });
    }
}
