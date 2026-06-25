using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mathom.Web.Contexts;

public record ContextView(Guid Id, string Name);

// User-scoped CRUD for contexts plus the user's "current context" pointer.
public class ContextService(MathomDbContext db)
{
    public async Task<IReadOnlyList<ContextView>> ListAsync(string userId, CancellationToken ct)
        => await db.Contexts
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.Name)
            .Select(c => new ContextView(c.Id, c.Name))
            .ToListAsync(ct);

    public async Task<Guid?> GetCurrentAsync(string userId, CancellationToken ct)
        => await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.CurrentContextId)
            .FirstOrDefaultAsync(ct);

    public async Task<ContextView?> CreateAsync(string userId, string? name, CancellationToken ct)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) return null;

        var lower = name.ToLowerInvariant();
        if (await db.Contexts.AnyAsync(c => c.UserId == userId && c.Name.ToLower() == lower, ct))
            return null;

        var ctx = new Context { Id = Guid.NewGuid(), UserId = userId, Name = name, CreatedAt = DateTimeOffset.UtcNow };
        db.Contexts.Add(ctx);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) // unique index race
        {
            db.ChangeTracker.Clear();
            return null;
        }
        return new ContextView(ctx.Id, ctx.Name);
    }

    public async Task<bool> RenameAsync(string userId, Guid id, string? name, CancellationToken ct)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) return false;

        var ctx = await db.Contexts.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);
        if (ctx is null) return false;

        var lower = name.ToLowerInvariant();
        if (await db.Contexts.AnyAsync(c => c.UserId == userId && c.Id != id && c.Name.ToLower() == lower, ct))
            return false;

        ctx.Name = name;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(string userId, Guid id, CancellationToken ct)
    {
        var ctx = await db.Contexts.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);
        if (ctx is null) return false;
        // FK rules handle the fan-out: Item.ContextId -> NULL, GlossaryTerm cascade,
        // ApplicationUser.CurrentContextId -> NULL.
        db.Contexts.Remove(ctx);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetCurrentAsync(string userId, Guid? contextId, CancellationToken ct)
    {
        if (contextId is { } id && !await db.Contexts.AnyAsync(c => c.Id == id && c.UserId == userId, ct))
            return false;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return false;
        user.CurrentContextId = contextId;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
