using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Mathom.Web.Domain;

namespace Mathom.Web.Data;

public class MathomDbContext(DbContextOptions<MathomDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Item> Items => Set<Item>()!;
    public DbSet<Tag> Tags => Set<Tag>()!;
    public DbSet<ItemTag> ItemTags => Set<ItemTag>()!;
    public DbSet<ItemPhoto> ItemPhotos => Set<ItemPhoto>()!;
    public DbSet<GlossaryTerm> GlossaryTerms => Set<GlossaryTerm>()!;
    public DbSet<GlossaryVariant> GlossaryVariants => Set<GlossaryVariant>()!;

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Item>(e =>
        {
            e.HasKey(x => x.Id);

            // Soft delete: trashed notes are hidden from every normal query.
            e.HasQueryFilter(x => x.DeletedAt == null);

            e.Property(x => x.RawText).IsRequired();
            e.Property(x => x.IdempotencyKey).IsRequired();
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => x.Status);
            e.Property(x => x.UserId).IsRequired();
            e.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            // Per-user timeline ordering.
            e.HasIndex(x => new { x.UserId, x.CreatedAt });

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

        b.Entity<ItemPhoto>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.MediaPath).IsRequired();
            e.HasOne(x => x.Item)
                .WithMany(i => i.Photos)
                .HasForeignKey(x => x.ItemId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ItemId);
        });

        b.Entity<GlossaryTerm>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).IsRequired();
            e.Property(x => x.Term).IsRequired();
            e.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.Term }).IsUnique();
            e.Property(x => x.Description).HasMaxLength(500);
        });

        b.Entity<GlossaryVariant>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Text).IsRequired();
            e.HasOne<GlossaryTerm>()
                .WithMany(t => t.Variants)
                .HasForeignKey(x => x.GlossaryTermId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.GlossaryTermId, x.Text }).IsUnique();
        });
    }
}
