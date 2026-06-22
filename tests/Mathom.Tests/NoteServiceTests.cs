using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Mathom.Web.Notes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class NoteServiceTests
{
    private readonly PostgresFixture _fx;
    public NoteServiceTests(PostgresFixture fx) => _fx = fx;

    private async Task<Item> SeedReadyAsync(string userId, string title, string? media = null, params string[] tags)
    {
        await _fx.EnsureUserAsync(userId, userId + "@example.com");
        await using var db = _fx.NewDbContext();
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

        await using (var db = _fx.NewDbContext())
        {
            var ok = await new NoteService(db, new FakeMediaStore()).UpdateAsync(
                u, item.Id, "New title", "new body", ItemType.Task, true,
                new[] { "keep", "added" }, CancellationToken.None);
            Assert.True(ok);
        }

        await using var verify = _fx.NewDbContext();
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
        await _fx.EnsureUserAsync(u, u + "@example.com");
        var pending = new Item
        {
            Id = Guid.NewGuid(), Status = ItemStatus.Pending, SourceType = SourceType.Text,
            RawText = "x", IdempotencyKey = Guid.NewGuid().ToString(),
            CreatedAt = DateTimeOffset.UtcNow, UserId = u,
        };
        await using (var seed = _fx.NewDbContext()) { seed.Items.Add(pending); await seed.SaveChangesAsync(); }

        await using var db = _fx.NewDbContext();
        var ok = await new NoteService(db, new FakeMediaStore())
            .UpdateAsync(u, pending.Id, "t", "b", ItemType.Note, false, Array.Empty<string>(), CancellationToken.None);
        Assert.False(ok);  // not Ready
    }

    [Fact]
    public async Task SoftDelete_Then_Restore_RoundTrips()
    {
        var u = "ns-softdelete-user";
        var item = await SeedReadyAsync(u, "Trash me");

        await using (var db = _fx.NewDbContext())
            Assert.True(await new NoteService(db, new FakeMediaStore()).SoftDeleteAsync(u, item.Id, CancellationToken.None));

        // Hidden from normal queries, present in trash.
        await using (var db = _fx.NewDbContext())
        {
            Assert.Empty(await db.Items.Where(i => i.Id == item.Id).ToListAsync());
            var trash = await new NoteService(db, new FakeMediaStore()).TrashAsync(u, 50, CancellationToken.None);
            Assert.Contains(trash, t => t.Id == item.Id);
        }

        await using (var db = _fx.NewDbContext())
            Assert.True(await new NoteService(db, new FakeMediaStore()).RestoreAsync(u, item.Id, CancellationToken.None));

        await using (var verify = _fx.NewDbContext())
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

        await using (var db = _fx.NewDbContext())
            Assert.True(await new NoteService(db, media).SoftDeleteAsync(u, item.Id, CancellationToken.None));
        await using (var db = _fx.NewDbContext())
            Assert.True(await new NoteService(db, media).PurgeAsync(u, item.Id, CancellationToken.None));

        await using var verify = _fx.NewDbContext();
        Assert.Empty(await verify.Items.IgnoreQueryFilters().Where(i => i.Id == item.Id).ToListAsync());
        Assert.False(media.Has(key));  // audio deleted
    }

    [Fact]
    public async Task Mutations_AreUserScoped()
    {
        var owner = "ns-owner";
        var attacker = "ns-attacker";
        await _fx.EnsureUserAsync(attacker, attacker + "@example.com");
        var item = await SeedReadyAsync(owner, "Owned");

        // Phase 1 — note is live (DeletedAt == null).
        // Both UpdateAsync and SoftDeleteAsync would succeed for the owner in this state,
        // but must be blocked for the attacker.
        await using (var db = _fx.NewDbContext())
        {
            var svc = new NoteService(db, new FakeMediaStore());
            Assert.False(await svc.UpdateAsync(attacker, item.Id, "x", "y", ItemType.Note, false, Array.Empty<string>(), CancellationToken.None));
            Assert.False(await svc.SoftDeleteAsync(attacker, item.Id, CancellationToken.None));
        }

        // Owner soft-deletes the note so it is now trashed (DeletedAt != null).
        await using (var db = _fx.NewDbContext())
            Assert.True(await new NoteService(db, new FakeMediaStore()).SoftDeleteAsync(owner, item.Id, CancellationToken.None));

        // Phase 2 — note is trashed (DeletedAt != null).
        // Both RestoreAsync and PurgeAsync would succeed for the owner in this state,
        // but must be blocked for the attacker.
        await using (var db = _fx.NewDbContext())
        {
            var svc = new NoteService(db, new FakeMediaStore());
            Assert.False(await svc.RestoreAsync(attacker, item.Id, CancellationToken.None));
            Assert.False(await svc.PurgeAsync(attacker, item.Id, CancellationToken.None));
        }

        // Final state: the row still exists, title is unchanged, and it remains trashed
        // (attacker neither updated the title nor restored/purged it).
        await using var verify = _fx.NewDbContext();
        var saved = await verify.Items.IgnoreQueryFilters().FirstAsync(i => i.Id == item.Id);
        Assert.Equal("Owned", saved.Title);     // attacker never updated it
        Assert.NotNull(saved.DeletedAt);        // attacker never restored or purged it; still in owner's trash
    }
}
