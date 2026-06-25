using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Mathom.Web.Admin;

public record UserRow(string Id, string Email, bool IsApproved);

// Approval reads/writes over the Identity user table. User-management only.
public class UserAdminService(MathomDbContext db)
{
    public async Task<bool> IsApprovedAsync(string userId, CancellationToken ct)
        => await db.Users.Where(u => u.Id == userId).Select(u => u.IsApproved).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<UserRow>> ListUsersAsync(CancellationToken ct)
        => await db.Users
            .OrderBy(u => u.IsApproved)            // false (pending) first
            .ThenBy(u => u.Email)
            .Select(u => new UserRow(u.Id, u.Email!, u.IsApproved))
            .ToListAsync(ct);

    public async Task<bool> ApproveAsync(string userId, CancellationToken ct)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == userId, ct);
        if (u is null) return false;
        u.IsApproved = true;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RevokeAsync(string actingUserId, string targetUserId, CancellationToken ct)
    {
        if (actingUserId == targetUserId) return false;   // never revoke yourself (lockout guard)
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == targetUserId, ct);
        if (u is null) return false;
        u.IsApproved = false;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
