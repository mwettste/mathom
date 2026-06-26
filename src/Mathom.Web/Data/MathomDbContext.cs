using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Mathom.Web.Domain;

namespace Mathom.Web.Data;

public class MathomDbContext(DbContextOptions<MathomDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Context> Contexts => Set<Context>()!;
    public DbSet<Item> Items => Set<Item>()!;
    public DbSet<Tag> Tags => Set<Tag>()!;
    public DbSet<ItemTag> ItemTags => Set<ItemTag>()!;
    public DbSet<ItemPhoto> ItemPhotos => Set<ItemPhoto>()!;
    public DbSet<ItemTranslation> ItemTranslations => Set<ItemTranslation>()!;
    public DbSet<GlossaryTerm> GlossaryTerms => Set<GlossaryTerm>()!;
    public DbSet<GlossaryVariant> GlossaryVariants => Set<GlossaryVariant>()!;
    public DbSet<UserLanguage> UserLanguages => Set<UserLanguage>()!;

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Item>(e =>
        {
            e.HasKey(x => x.Id);

            // Soft delete: trashed notes are hidden from every normal query.
            e.HasQueryFilter(x => x.DeletedAt == null);

            e.Property(x => x.RawText).IsRequired();
            e.Property(x => x.CaptureNote).HasMaxLength(4000);
            e.Property(x => x.IdempotencyKey).IsRequired();
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => x.Status);
            e.Property(x => x.UserId).IsRequired();
            e.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            // Deleting a context reassigns its items to Inbox.
            e.HasOne<Context>()
                .WithMany()
                .HasForeignKey(x => x.ContextId)
                .OnDelete(DeleteBehavior.SetNull);
            // Per-user timeline ordering.
            e.HasIndex(x => new { x.UserId, x.CreatedAt });

            // Generated tsvector over Title + CleanText for full-text search.
#pragma warning disable CS8603
            e.HasGeneratedTsVectorColumn(
                    x => x.SearchVector,
                    "simple",
                    x => new { x.Title, x.CleanText })
                .HasIndex(x => x.SearchVector)
                .HasMethod("GIN");
#pragma warning restore CS8603

            // Semantic-search embedding. Fixed dimension (EmbeddingConfig.Dimensions); the
            // HNSW cosine index is created in the AddItemEmbedding migration (raw SQL).
            e.Property(x => x.Embedding)
                .HasColumnType($"vector({Mathom.Web.Embeddings.EmbeddingConfig.Dimensions})");
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
            e.Property(x => x.ExternalId).IsRequired();
            e.HasOne(x => x.Item)
                .WithMany(i => i.Photos)
                .HasForeignKey(x => x.ItemId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ItemId);
            e.HasIndex(x => x.ExternalId).IsUnique();
        });

        b.Entity<ItemTranslation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Locale).IsRequired();
            e.Property(x => x.Title).IsRequired();
            e.Property(x => x.CleanText).IsRequired();
            e.HasOne(x => x.Item)
                .WithMany(i => i.Translations)
                .HasForeignKey(x => x.ItemId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ItemId, x.Locale }).IsUnique();

            // Per-locale generated tsvector for full-text search across translations.
#pragma warning disable CS8603
            e.HasGeneratedTsVectorColumn(
                    x => x.SearchVector,
                    "simple",
                    x => new { x.Title, x.CleanText })
                .HasIndex(x => x.SearchVector)
                .HasMethod("GIN");
#pragma warning restore CS8603
        });

        b.Entity<Context>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).IsRequired();
            e.Property(x => x.Name).IsRequired();
            e.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            // Per-user name uniqueness (exact match). Case-insensitive dup rejection
            // is enforced in ContextService, mirroring the Glossary pattern.
            e.HasIndex(x => new { x.UserId, x.Name }).IsUnique();
        });

        b.Entity<ApplicationUser>(e =>
        {
            // Deleting the current context drops the user back to Inbox.
            e.HasOne<Context>()
                .WithMany()
                .HasForeignKey(x => x.CurrentContextId)
                .OnDelete(DeleteBehavior.SetNull);
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
            // A context's glossary terms are deleted with it; Inbox terms (null) survive.
            e.HasOne<Context>()
                .WithMany()
                .HasForeignKey(x => x.ContextId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.ContextId, x.Term }).IsUnique();
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

        b.Entity<UserLanguage>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).IsRequired();
            e.Property(x => x.Locale).IsRequired();
            e.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.Locale }).IsUnique();
        });
    }
}
