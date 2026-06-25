using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mathom.Web.Glossary;

public record GlossaryEntry(string Term, IReadOnlyList<string> Variants, string? Description);
public record GlossaryVariantView(Guid Id, string Text);
public record GlossaryTermView(Guid Id, string Term, IReadOnlyList<GlossaryVariantView> Variants, string? Description);
public record GlossaryDescription(Guid Id, string? Description);

// User-scoped per-user glossary of correct domain terms.
public class GlossaryService(MathomDbContext db)
{
    public async Task<IReadOnlyList<string>> GetTermsAsync(string userId, Guid? contextId, CancellationToken ct)
    {
        var q = db.GlossaryTerms.Where(g => g.UserId == userId);
        q = contextId is { } cid ? q.Where(g => g.ContextId == cid) : q.Where(g => g.ContextId == null);
        return await q
            .OrderBy(g => g.CreatedAt)
            .Select(g => g.Term)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<(Guid Id, string Term)>> GetTermRowsAsync(string userId, Guid? contextId, CancellationToken ct)
    {
        var q = db.GlossaryTerms.Where(g => g.UserId == userId);
        q = contextId is { } cid ? q.Where(g => g.ContextId == cid) : q.Where(g => g.ContextId == null);
        return await q
            .OrderBy(g => g.CreatedAt)
            .Select(g => new ValueTuple<Guid, string>(g.Id, g.Term))
            .ToListAsync(ct);
    }

    public async Task<bool> AddAsync(string userId, Guid? contextId, string term, string? variant, CancellationToken ct)
    {
        term = (term ?? string.Empty).Trim();
        if (term.Length == 0) return false;

        var lower = term.ToLowerInvariant();
        var existing = contextId is { } cid
            ? await db.GlossaryTerms.FirstOrDefaultAsync(g => g.UserId == userId && g.ContextId == cid && g.Term.ToLower() == lower, ct)
            : await db.GlossaryTerms.FirstOrDefaultAsync(g => g.UserId == userId && g.ContextId == null && g.Term.ToLower() == lower, ct);
        var changed = false;
        if (existing is null)
        {
            existing = new GlossaryTerm { Id = Guid.NewGuid(), UserId = userId, ContextId = contextId, Term = term, CreatedAt = DateTimeOffset.UtcNow };
            db.GlossaryTerms.Add(existing);
            changed = true;
        }

        var v = (variant ?? string.Empty).Trim();
        if (v.Length > 0 && !string.Equals(v, existing.Term, StringComparison.OrdinalIgnoreCase))
        {
            var vlower = v.ToLowerInvariant();
            var hasVariant = await db.GlossaryVariants
                .AnyAsync(x => x.GlossaryTermId == existing.Id && x.Text.ToLower() == vlower, ct);
            if (!hasVariant)
            {
                db.GlossaryVariants.Add(new GlossaryVariant
                {
                    Id = Guid.NewGuid(), GlossaryTermId = existing.Id, Text = v, CreatedAt = DateTimeOffset.UtcNow,
                });
                changed = true;
            }
        }

        if (!changed) return false;
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<IReadOnlyList<GlossaryEntry>> GetEntriesAsync(string userId, Guid? contextId, CancellationToken ct)
    {
        var q = db.GlossaryTerms.Where(g => g.UserId == userId);
        q = contextId is { } cid ? q.Where(g => g.ContextId == cid) : q.Where(g => g.ContextId == null);
        return await q
            .OrderBy(g => g.CreatedAt)
            .Select(g => new GlossaryEntry(
                g.Term,
                g.Variants.OrderBy(v => v.CreatedAt).Select(v => v.Text).ToList(),
                g.Description))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<GlossaryTermView>> GetTermViewsAsync(string userId, Guid? contextId, CancellationToken ct)
    {
        var q = db.GlossaryTerms.Where(g => g.UserId == userId);
        q = contextId is { } cid ? q.Where(g => g.ContextId == cid) : q.Where(g => g.ContextId == null);
        return await q
            .OrderBy(g => g.CreatedAt)
            .Select(g => new GlossaryTermView(
                g.Id, g.Term,
                g.Variants.OrderBy(v => v.CreatedAt).Select(v => new GlossaryVariantView(v.Id, v.Text)).ToList(),
                g.Description))
            .ToListAsync(ct);
    }

    public async Task<GlossaryDescription?> GetDescriptionAsync(string userId, Guid termId, CancellationToken ct)
        => await db.GlossaryTerms
            .Where(g => g.Id == termId && g.UserId == userId)
            .Select(g => new GlossaryDescription(g.Id, g.Description))
            .FirstOrDefaultAsync(ct);

    public async Task<bool> SetDescriptionAsync(string userId, Guid termId, string? description, CancellationToken ct)
    {
        var term = await db.GlossaryTerms.FirstOrDefaultAsync(g => g.Id == termId && g.UserId == userId, ct);
        if (term is null) return false;
        var d = (description ?? string.Empty).Trim();
        if (d.Length > 500) d = d.Substring(0, 500);
        term.Description = d.Length == 0 ? null : d;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RemoveVariantAsync(string userId, Guid variantId, CancellationToken ct)
    {
        var v = await db.GlossaryVariants
            .Where(x => x.Id == variantId)
            .Where(x => db.GlossaryTerms.Any(t => t.Id == x.GlossaryTermId && t.UserId == userId))
            .FirstOrDefaultAsync(ct);
        if (v is null) return false;
        db.GlossaryVariants.Remove(v);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RemoveAsync(string userId, Guid id, CancellationToken ct)
    {
        var t = await db.GlossaryTerms.FirstOrDefaultAsync(g => g.Id == id && g.UserId == userId, ct);
        if (t is null) return false;
        db.GlossaryTerms.Remove(t);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
