using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mathom.Web.Glossary;

public record GlossaryEntry(string Term, IReadOnlyList<string> Variants);
public record GlossaryVariantView(Guid Id, string Text);
public record GlossaryTermView(Guid Id, string Term, IReadOnlyList<GlossaryVariantView> Variants);

// User-scoped per-user glossary of correct domain terms.
public class GlossaryService
{
    private readonly MathomDbContext _db;
    public GlossaryService(MathomDbContext db) => _db = db;

    public async Task<IReadOnlyList<string>> GetTermsAsync(string userId, CancellationToken ct)
        => await _db.GlossaryTerms
            .Where(g => g.UserId == userId)
            .OrderBy(g => g.CreatedAt)
            .Select(g => g.Term)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<(Guid Id, string Term)>> GetTermRowsAsync(string userId, CancellationToken ct)
        => await _db.GlossaryTerms
            .Where(g => g.UserId == userId)
            .OrderBy(g => g.CreatedAt)
            .Select(g => new ValueTuple<Guid, string>(g.Id, g.Term))
            .ToListAsync(ct);

    public async Task<bool> AddAsync(string userId, string term, string? variant, CancellationToken ct)
    {
        term = (term ?? string.Empty).Trim();
        if (term.Length == 0) return false;

        var lower = term.ToLowerInvariant();
        var existing = await _db.GlossaryTerms.FirstOrDefaultAsync(g => g.UserId == userId && g.Term.ToLower() == lower, ct);
        var changed = false;
        if (existing is null)
        {
            existing = new GlossaryTerm { Id = Guid.NewGuid(), UserId = userId, Term = term, CreatedAt = DateTimeOffset.UtcNow };
            _db.GlossaryTerms.Add(existing);
            changed = true;
        }

        var v = (variant ?? string.Empty).Trim();
        if (v.Length > 0 && !string.Equals(v, existing.Term, StringComparison.OrdinalIgnoreCase))
        {
            var vlower = v.ToLowerInvariant();
            var hasVariant = await _db.GlossaryVariants
                .AnyAsync(x => x.GlossaryTermId == existing.Id && x.Text.ToLower() == vlower, ct);
            if (!hasVariant)
            {
                _db.GlossaryVariants.Add(new GlossaryVariant
                {
                    Id = Guid.NewGuid(), GlossaryTermId = existing.Id, Text = v, CreatedAt = DateTimeOffset.UtcNow,
                });
                changed = true;
            }
        }

        if (!changed) return false;
        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<IReadOnlyList<GlossaryEntry>> GetEntriesAsync(string userId, CancellationToken ct)
        => await _db.GlossaryTerms
            .Where(g => g.UserId == userId)
            .OrderBy(g => g.CreatedAt)
            .Select(g => new GlossaryEntry(
                g.Term,
                g.Variants.OrderBy(v => v.CreatedAt).Select(v => v.Text).ToList()))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<GlossaryTermView>> GetTermViewsAsync(string userId, CancellationToken ct)
        => await _db.GlossaryTerms
            .Where(g => g.UserId == userId)
            .OrderBy(g => g.CreatedAt)
            .Select(g => new GlossaryTermView(
                g.Id, g.Term,
                g.Variants.OrderBy(v => v.CreatedAt).Select(v => new GlossaryVariantView(v.Id, v.Text)).ToList()))
            .ToListAsync(ct);

    public async Task<bool> RemoveVariantAsync(string userId, Guid variantId, CancellationToken ct)
    {
        var v = await _db.GlossaryVariants
            .Where(x => x.Id == variantId)
            .Where(x => _db.GlossaryTerms.Any(t => t.Id == x.GlossaryTermId && t.UserId == userId))
            .FirstOrDefaultAsync(ct);
        if (v is null) return false;
        _db.GlossaryVariants.Remove(v);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RemoveAsync(string userId, Guid id, CancellationToken ct)
    {
        var t = await _db.GlossaryTerms.FirstOrDefaultAsync(g => g.Id == id && g.UserId == userId, ct);
        if (t is null) return false;
        _db.GlossaryTerms.Remove(t);
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
