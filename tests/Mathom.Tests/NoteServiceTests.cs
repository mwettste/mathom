using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Mathom.Web.Media;
using Mathom.Web.Notes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class NoteServiceTests(PostgresFixture fx)
{
    private async Task<Item> SeedReadyAsync(string userId, string title, string? media = null, params string[] tags)
    {
        await fx.EnsureUserAsync(userId, userId + "@example.com");
        await using var db = fx.NewDbContext();
        var item = new Item
        {
            Id = Guid.NewGuid(), Status = ItemStatus.Ready, SourceType = media is null ? SourceType.Text : SourceType.Voice,
            RawText = title, CleanText = title, Title = title, ItemType = ItemType.Note,
            CreatedAt = DateTimeOffset.UtcNow, ProcessedAt = DateTimeOffset.UtcNow,
            IdempotencyKey = Guid.NewGuid().ToString(), UserId = userId, MediaPath = media,
        };
        foreach (var name in tags)
        {
            var tag = await db.Tags.FirstOrDefaultAsync(t => t.Name == name && t.Kind == TagKind.Topic)
                      ?? new Tag { Name = name, Kind = TagKind.Topic };
            item.ItemTags.Add(new ItemTag { Item = item, Tag = tag });
        }
        db.Items.Add(item);
        await db.SaveChangesAsync();
        return item;
    }

    [Fact]
    public async Task Update_ChangesFieldsAndReconcilesTags()
    {
        var u = "ns-update-user";
        var item = await SeedReadyAsync(u, "Old title", null, "keep", "drop");

        await using (var db = fx.NewDbContext())
        {
            var ok = await new NoteService(db, new FakeMediaStore()).UpdateAsync(
                u, item.Id, "New title", "new body", ItemType.Task, true,
                new[] { "keep", "added" }, CancellationToken.None);
            Assert.True(ok);
        }

        await using var verify = fx.NewDbContext();
        var saved = await verify.Items.Include(i => i.ItemTags).ThenInclude(t => t.Tag)
            .FirstAsync(i => i.Id == item.Id);
        Assert.Equal("New title", saved.Title);
        Assert.Equal("new body", saved.CleanText);
        Assert.Equal(ItemType.Task, saved.ItemType);
        Assert.True(saved.Actionable);
        var names = saved.ItemTags.Select(t => t.Tag.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "added", "keep" }, names);  // "drop" removed, "added" added
    }

    [Fact]
    public async Task Update_OnlyWorksOnReady()
    {
        var u = "ns-ready-user";
        await fx.EnsureUserAsync(u, u + "@example.com");
        var pending = new Item
        {
            Id = Guid.NewGuid(), Status = ItemStatus.Pending, SourceType = SourceType.Text,
            RawText = "x", IdempotencyKey = Guid.NewGuid().ToString(),
            CreatedAt = DateTimeOffset.UtcNow, UserId = u,
        };
        await using (var seed = fx.NewDbContext()) { seed.Items.Add(pending); await seed.SaveChangesAsync(); }

        await using var db = fx.NewDbContext();
        var ok = await new NoteService(db, new FakeMediaStore())
            .UpdateAsync(u, pending.Id, "t", "b", ItemType.Note, false, Array.Empty<string>(), CancellationToken.None);
        Assert.False(ok);  // not Ready
    }

    [Fact]
    public async Task SoftDelete_Then_Restore_RoundTrips()
    {
        var u = "ns-softdelete-user";
        var item = await SeedReadyAsync(u, "Trash me");

        await using (var db = fx.NewDbContext())
            Assert.True(await new NoteService(db, new FakeMediaStore()).SoftDeleteAsync(u, item.Id, CancellationToken.None));

        // Hidden from normal queries, present in trash.
        await using (var db = fx.NewDbContext())
        {
            Assert.Empty(await db.Items.Where(i => i.Id == item.Id).ToListAsync());
            var trash = await new NoteService(db, new FakeMediaStore()).TrashAsync(u, 50, CancellationToken.None);
            Assert.Contains(trash, t => t.Id == item.Id);
        }

        await using (var db = fx.NewDbContext())
            Assert.True(await new NoteService(db, new FakeMediaStore()).RestoreAsync(u, item.Id, CancellationToken.None));

        await using (var verify = fx.NewDbContext())
            Assert.Single(await verify.Items.Where(i => i.Id == item.Id).ToListAsync());  // back to live
    }

    [Fact]
    public async Task Purge_RemovesRowAndMedia()
    {
        var u = "ns-purge-user";
        var media = new FakeMediaStore();
        // Save a blob and attach its key to a voice note.
        using var ms = new System.IO.MemoryStream(new byte[] { 1, 2, 3 });
        var key = await media.SaveAsync(ms, ".webm", CancellationToken.None);
        var item = await SeedReadyAsync(u, "Voice note", media: key);

        await using (var db = fx.NewDbContext())
            Assert.True(await new NoteService(db, media).SoftDeleteAsync(u, item.Id, CancellationToken.None));
        await using (var db = fx.NewDbContext())
            Assert.True(await new NoteService(db, media).PurgeAsync(u, item.Id, CancellationToken.None));

        await using var verify = fx.NewDbContext();
        Assert.Empty(await verify.Items.IgnoreQueryFilters().Where(i => i.Id == item.Id).ToListAsync());
        Assert.False(media.Has(key));  // audio deleted
    }

    [Fact]
    public async Task Purge_DeletesAllPhotoMedia()
    {
        var u = "purge-photos-user";
        await fx.EnsureUserAsync(u, u + "@example.com");
        var media = new FakeMediaStore();
        string k1, k2;
        using (var a = new System.IO.MemoryStream(new byte[] { 1 })) k1 = await media.SaveAsync(a, ".jpg", CancellationToken.None);
        using (var b = new System.IO.MemoryStream(new byte[] { 2 })) k2 = await media.SaveAsync(b, ".png", CancellationToken.None);

        var item = Item.CreatePending(SourceType.Photo, "", Guid.NewGuid().ToString(), u, DateTimeOffset.UtcNow);
        item.DeletedAt = DateTimeOffset.UtcNow;   // already in trash, so PurgeAsync accepts it
        item.Photos.Add(new ItemPhoto { Id = Guid.NewGuid(), MediaPath = k1, Order = 0, CreatedAt = DateTimeOffset.UtcNow });
        item.Photos.Add(new ItemPhoto { Id = Guid.NewGuid(), MediaPath = k2, Order = 1, CreatedAt = DateTimeOffset.UtcNow });
        await using (var seed = fx.NewDbContext()) { seed.Items.Add(item); await seed.SaveChangesAsync(); }

        await using (var db = fx.NewDbContext())
            Assert.True(await new NoteService(db, media).PurgeAsync(u, item.Id, CancellationToken.None));

        Assert.False(media.Has(k1));
        Assert.False(media.Has(k2));
        await using (var verify = fx.NewDbContext())
        {
            Assert.False(await verify.Items.IgnoreQueryFilters().AnyAsync(i => i.Id == item.Id));
            Assert.False(await verify.Set<ItemPhoto>().AnyAsync(p => p.ItemId == item.Id));
        }
    }

    [Fact]
    public async Task Reprocess_SetsReadyOrFailed_ToPending()
    {
        var u = "reprocess-user";
        var ready = await SeedReadyAsync(u, "ready note");

        await using (var db = fx.NewDbContext())
            Assert.True(await new NoteService(db, new FakeMediaStore()).ReprocessAsync(u, ready.Id, CancellationToken.None));

        await using var v1 = fx.NewDbContext();
        Assert.Equal(ItemStatus.Pending, (await v1.Items.FirstAsync(i => i.Id == ready.Id)).Status);

        // A Failed note re-processes too, clearing the error.
        await using (var db = fx.NewDbContext())
        {
            var failed = await db.Items.FirstAsync(i => i.Id == ready.Id);
            failed.Status = ItemStatus.Failed; failed.Error = "boom";
            await db.SaveChangesAsync();
        }
        await using (var db = fx.NewDbContext())
            Assert.True(await new NoteService(db, new FakeMediaStore()).ReprocessAsync(u, ready.Id, CancellationToken.None));
        await using var v2 = fx.NewDbContext();
        var after = await v2.Items.FirstAsync(i => i.Id == ready.Id);
        Assert.Equal(ItemStatus.Pending, after.Status);
        Assert.Null(after.Error);
    }

    [Fact]
    public async Task Reprocess_NoOp_OnInFlight_And_CrossUser()
    {
        var owner = "reprocess-owner";
        var attacker = "reprocess-attacker";
        await fx.EnsureUserAsync(attacker, attacker + "@example.com");
        var ready = await SeedReadyAsync(owner, "owned note");

        await using var db = fx.NewDbContext();
        var svc = new NoteService(db, new FakeMediaStore());

        // Cross-user: attacker cannot re-process the owner's note.
        Assert.False(await svc.ReprocessAsync(attacker, ready.Id, CancellationToken.None));

        // In-flight: set Processing, then re-process is a no-op.
        var item = await db.Items.FirstAsync(i => i.Id == ready.Id);
        item.Status = ItemStatus.Processing; await db.SaveChangesAsync();
        await using var db2 = fx.NewDbContext();
        Assert.False(await new NoteService(db2, new FakeMediaStore()).ReprocessAsync(owner, ready.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Purge_OneMediaDeleteFails_StillDeletesOthersAndSucceeds()
    {
        var u = "purge-besteff-user";
        await fx.EnsureUserAsync(u, u + "@example.com");
        var inner = new FakeMediaStore();
        string k1, k2;
        using (var a = new System.IO.MemoryStream(new byte[] { 1 })) k1 = await inner.SaveAsync(a, ".jpg", CancellationToken.None);
        using (var b = new System.IO.MemoryStream(new byte[] { 2 })) k2 = await inner.SaveAsync(b, ".png", CancellationToken.None);

        // k1 is the poison key: DeleteAsync will throw for it.
        var media = new PoisonKeyMediaStore(inner, poisonKey: k1);

        var item = Item.CreatePending(SourceType.Photo, "", Guid.NewGuid().ToString(), u, DateTimeOffset.UtcNow);
        item.DeletedAt = DateTimeOffset.UtcNow;
        item.Photos.Add(new ItemPhoto { Id = Guid.NewGuid(), MediaPath = k1, Order = 0, CreatedAt = DateTimeOffset.UtcNow });
        item.Photos.Add(new ItemPhoto { Id = Guid.NewGuid(), MediaPath = k2, Order = 1, CreatedAt = DateTimeOffset.UtcNow });
        await using (var seed = fx.NewDbContext()) { seed.Items.Add(item); await seed.SaveChangesAsync(); }

        bool result;
        await using (var db = fx.NewDbContext())
            result = await new NoteService(db, media).PurgeAsync(u, item.Id, CancellationToken.None);

        // PurgeAsync must return true even though k1 delete threw.
        Assert.True(result);
        // k2 (non-poison) must have been deleted.
        Assert.False(inner.Has(k2));
        // DB rows must be gone.
        await using var verify = fx.NewDbContext();
        Assert.False(await verify.Items.IgnoreQueryFilters().AnyAsync(i => i.Id == item.Id));
        Assert.False(await verify.Set<ItemPhoto>().AnyAsync(p => p.ItemId == item.Id));
    }

    [Fact]
    public async Task Purge_DeletesBothOriginalAndDisplayVariantFiles()
    {
        var media = new FakeMediaStore();
        string orig, disp;
        using (var a = new System.IO.MemoryStream(new byte[] { 1, 2, 3 })) orig = await media.SaveAsync(a, ".jpg", CancellationToken.None);
        using (var b = new System.IO.MemoryStream(new byte[] { 4, 5, 6 })) disp = await media.SaveAsync(b, ".jpg", CancellationToken.None);

        var u = "purge-display-variant-user";
        await fx.EnsureUserAsync(u, u + "@example.com");
        var item = Item.CreatePending(SourceType.Photo, "", Guid.NewGuid().ToString(), u, DateTimeOffset.UtcNow);
        item.DeletedAt = DateTimeOffset.UtcNow;
        item.Photos.Add(new ItemPhoto { MediaPath = orig, DisplayPath = disp, Order = 0, CreatedAt = DateTimeOffset.UtcNow });
        Guid id;
        await using (var db = fx.NewDbContext()) { db.Items.Add(item); await db.SaveChangesAsync(); id = item.Id; }

        await using (var db = fx.NewDbContext())
        {
            var svc = new NoteService(db, media);
            var ok = await svc.PurgeAsync(u, id, CancellationToken.None);
            Assert.True(ok);
        }

        Assert.False(media.Has(orig));
        Assert.False(media.Has(disp));
    }

    [Fact]
    public async Task Mutations_AreUserScoped()
    {
        var owner = "ns-owner";
        var attacker = "ns-attacker";
        await fx.EnsureUserAsync(attacker, attacker + "@example.com");
        var item = await SeedReadyAsync(owner, "Owned");

        // Phase 1 — note is live (DeletedAt == null).
        // Both UpdateAsync and SoftDeleteAsync would succeed for the owner in this state,
        // but must be blocked for the attacker.
        await using (var db = fx.NewDbContext())
        {
            var svc = new NoteService(db, new FakeMediaStore());
            Assert.False(await svc.UpdateAsync(attacker, item.Id, "x", "y", ItemType.Note, false, Array.Empty<string>(), CancellationToken.None));
            Assert.False(await svc.SoftDeleteAsync(attacker, item.Id, CancellationToken.None));
        }

        // Owner soft-deletes the note so it is now trashed (DeletedAt != null).
        await using (var db = fx.NewDbContext())
            Assert.True(await new NoteService(db, new FakeMediaStore()).SoftDeleteAsync(owner, item.Id, CancellationToken.None));

        // Phase 2 — note is trashed (DeletedAt != null).
        // Both RestoreAsync and PurgeAsync would succeed for the owner in this state,
        // but must be blocked for the attacker.
        await using (var db = fx.NewDbContext())
        {
            var svc = new NoteService(db, new FakeMediaStore());
            Assert.False(await svc.RestoreAsync(attacker, item.Id, CancellationToken.None));
            Assert.False(await svc.PurgeAsync(attacker, item.Id, CancellationToken.None));
        }

        // Final state: the row still exists, title is unchanged, and it remains trashed
        // (attacker neither updated the title nor restored/purged it).
        await using var verify = fx.NewDbContext();
        var saved = await verify.Items.IgnoreQueryFilters().FirstAsync(i => i.Id == item.Id);
        Assert.Equal("Owned", saved.Title);     // attacker never updated it
        Assert.NotNull(saved.DeletedAt);        // attacker never restored or purged it; still in owner's trash
    }
}

/// <summary>
/// Delegates all IMediaStore calls to an inner FakeMediaStore except DeleteAsync for
/// a single "poison" key, which throws — used to prove best-effort purge behaviour.
/// </summary>
file sealed class PoisonKeyMediaStore(FakeMediaStore inner, string poisonKey) : IMediaStore
{
    public Task<string> SaveAsync(System.IO.Stream content, string fileExtension, CancellationToken ct)
        => inner.SaveAsync(content, fileExtension, ct);

    public Task<System.IO.Stream> OpenReadAsync(string mediaPath, CancellationToken ct)
        => inner.OpenReadAsync(mediaPath, ct);

    public Task DeleteAsync(string mediaPath, CancellationToken ct)
    {
        if (mediaPath == poisonKey)
            throw new InvalidOperationException($"Simulated delete failure for key '{mediaPath}'.");
        return inner.DeleteAsync(mediaPath, ct);
    }
}
