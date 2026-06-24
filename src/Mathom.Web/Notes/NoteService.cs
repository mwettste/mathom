using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Mathom.Web.Media;
using Mathom.Web.Search;
using Microsoft.EntityFrameworkCore;

namespace Mathom.Web.Notes;

// All mutations are user-scoped: they match Id == id && UserId == userId and
// return false / empty for another user's note.
public class NoteService
{
    private readonly MathomDbContext _db;
    private readonly IMediaStore _media;
    public NoteService(MathomDbContext db, IMediaStore media)
    {
        _db = db;
        _media = media;
    }

    public async Task<bool> ReprocessAsync(string userId, Guid id, CancellationToken ct)
    {
        var item = await _db.Items.FirstOrDefaultAsync(
            i => i.Id == id && i.UserId == userId
              && (i.Status == ItemStatus.Ready || i.Status == ItemStatus.Failed), ct);
        if (item is null) return false;
        item.Status = ItemStatus.Pending;
        item.Error = null;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> UpdateAsync(string userId, Guid id, string? title, string? body,
        ItemType? type, bool actionable, IReadOnlyList<string> tagNames, CancellationToken ct)
    {
        var item = await _db.Items
            .Include(i => i.ItemTags).ThenInclude(it => it.Tag)
            .FirstOrDefaultAsync(i => i.Id == id && i.UserId == userId && i.Status == ItemStatus.Ready, ct);
        if (item is null) return false;

        item.Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        item.CleanText = string.IsNullOrWhiteSpace(body) ? null : body.Trim();
        item.ItemType = type;
        item.Actionable = actionable;

        await ReconcileTagsAsync(item, tagNames, ct);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SoftDeleteAsync(string userId, Guid id, CancellationToken ct)
    {
        var item = await _db.Items.FirstOrDefaultAsync(i => i.Id == id && i.UserId == userId, ct);
        if (item is null) return false;
        item.DeletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RestoreAsync(string userId, Guid id, CancellationToken ct)
    {
        var item = await _db.Items.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == id && i.UserId == userId && i.DeletedAt != null, ct);
        if (item is null) return false;
        item.DeletedAt = null;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> PurgeAsync(string userId, Guid id, CancellationToken ct)
    {
        var item = await _db.Items.IgnoreQueryFilters().Include(i => i.Photos)
            .FirstOrDefaultAsync(i => i.Id == id && i.UserId == userId && i.DeletedAt != null, ct);
        if (item is null) return false;

        // Collect every disk key before removing the row; the DB cascade drops ItemTags and
        // ItemPhoto rows, but media files must be deleted explicitly.
        var mediaKeys = new List<string>();
        if (!string.IsNullOrEmpty(item.MediaPath)) mediaKeys.Add(item.MediaPath);
        mediaKeys.AddRange(item.Photos.Where(p => !string.IsNullOrEmpty(p.MediaPath)).Select(p => p.MediaPath));

        _db.Items.Remove(item);                 // cascades ItemTags + ItemPhoto rows
        await _db.SaveChangesAsync(ct);

        // Best-effort per file: the DB row is already gone, so a failed media delete must not
        // fail the purge or block deleting the remaining files. Orphaned blobs are
        // acceptable over a failed purge.
        foreach (var key in mediaKeys)
        {
            try { await _media.DeleteAsync(key, ct); }
            catch { /* swallow: best-effort cleanup */ }
        }

        return true;
    }

    public async Task<IReadOnlyList<ItemSummary>> TrashAsync(string userId, int take, CancellationToken ct)
    {
        return await _db.Items.IgnoreQueryFilters()
            .Where(i => i.UserId == userId && i.DeletedAt != null)
            .OrderByDescending(i => i.DeletedAt)
            .Take(take)
            .Select(i => new ItemSummary(
                i.Id, i.Title, i.CleanText, i.ItemType, i.CreatedAt,
                i.Status, i.SourceType, i.Actionable,
                i.ItemTags.Select(it => it.Tag.Name).ToList()))
            .ToListAsync(ct);
    }

    private async Task ReconcileTagsAsync(Item item, IReadOnlyList<string> tagNames, CancellationToken ct)
    {
        // Normalize: trim, drop empties, dedupe case-insensitively (keep first spelling).
        var desired = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in tagNames)
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            if (seen.Add(name)) desired.Add(name);
        }

        // Remove links no longer desired.
        item.ItemTags.RemoveAll(it =>
            !desired.Any(d => string.Equals(d, it.Tag.Name, StringComparison.OrdinalIgnoreCase)));

        // Add links for new names (get-or-create the Topic tag).
        foreach (var name in desired)
        {
            if (item.ItemTags.Any(it => string.Equals(it.Tag.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            var tag = await _db.Tags.FirstOrDefaultAsync(t => t.Name == name && t.Kind == TagKind.Topic, ct)
                      ?? new Tag { Name = name, Kind = TagKind.Topic };
            item.ItemTags.Add(new ItemTag { Item = item, Tag = tag });
        }
    }
}
