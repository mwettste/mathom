using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mathom.Web.Glossary;

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

    public async Task<bool> AddAsync(string userId, string term, CancellationToken ct)
    {
        term = (term ?? string.Empty).Trim();
        if (term.Length == 0) return false;

        var lower = term.ToLower();
        var exists = await _db.GlossaryTerms.AnyAsync(g => g.UserId == userId && g.Term.ToLower() == lower, ct);
        if (exists) return false;

        _db.GlossaryTerms.Add(new GlossaryTerm
        {
            Id = Guid.NewGuid(), UserId = userId, Term = term, CreatedAt = DateTimeOffset.UtcNow,
        });
        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            return false; // unique (UserId, Term) race — treat as no-op
        }
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
