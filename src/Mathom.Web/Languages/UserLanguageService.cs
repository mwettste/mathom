// src/Mathom.Web/Languages/UserLanguageService.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mathom.Web.Languages;

public record UserLanguageView(Guid Id, string Locale, string DisplayName, bool IsPrimary, int SortOrder);

// User-scoped set of working languages. Exactly one row is primary.
public class UserLanguageService(MathomDbContext db)
{
    private IQueryable<UserLanguage> ForUser(string userId)
        => db.UserLanguages.Where(x => x.UserId == userId);

    // Configured locales, primary first then by SortOrder. Empty when none configured.
    public async Task<IReadOnlyList<string>> GetActiveLocalesAsync(string userId, CancellationToken ct)
        => await ForUser(userId)
            .OrderByDescending(x => x.IsPrimary).ThenBy(x => x.SortOrder)
            .Select(x => x.Locale)
            .ToListAsync(ct);

    public async Task<string> GetPrimaryLocaleAsync(string userId, CancellationToken ct)
        => await ForUser(userId).Where(x => x.IsPrimary).Select(x => x.Locale).FirstOrDefaultAsync(ct)
           ?? "en";

    public async Task<IReadOnlyList<UserLanguageView>> GetViewsAsync(string userId, CancellationToken ct)
    {
        var rows = await ForUser(userId)
            .OrderByDescending(x => x.IsPrimary).ThenBy(x => x.SortOrder)
            .ToListAsync(ct);
        return rows.Select(x => new UserLanguageView(x.Id, x.Locale, Locales.DisplayName(x.Locale), x.IsPrimary, x.SortOrder)).ToList();
    }

    public async Task<bool> AddAsync(string userId, string locale, CancellationToken ct)
    {
        if (!Locales.IsSupported(locale)) return false;
        if (await ForUser(userId).AnyAsync(x => x.Locale == locale, ct)) return false;

        var any = await ForUser(userId).AnyAsync(ct);
        var maxOrder = any ? await ForUser(userId).MaxAsync(x => x.SortOrder, ct) : -1;
        db.UserLanguages.Add(new UserLanguage
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Locale = locale,
            IsPrimary = !any,            // first one configured becomes primary
            SortOrder = maxOrder + 1,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return false; }
    }

    public async Task<bool> SetPrimaryAsync(string userId, string locale, CancellationToken ct)
    {
        var rows = await ForUser(userId).ToListAsync(ct);
        var target = rows.FirstOrDefault(x => x.Locale == locale);
        if (target is null) return false;
        foreach (var r in rows) r.IsPrimary = ReferenceEquals(r, target);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RemoveAsync(string userId, string locale, CancellationToken ct)
    {
        var rows = await ForUser(userId).ToListAsync(ct);
        var target = rows.FirstOrDefault(x => x.Locale == locale);
        if (target is null) return false;
        db.UserLanguages.Remove(target);
        if (target.IsPrimary)
        {
            var next = rows.Where(x => !ReferenceEquals(x, target)).OrderBy(x => x.SortOrder).FirstOrDefault();
            if (next is not null) next.IsPrimary = true;
        }
        await db.SaveChangesAsync(ct);
        return true;
    }
}
