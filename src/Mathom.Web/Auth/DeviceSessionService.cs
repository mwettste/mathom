using System.Security.Cryptography;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mathom.Web.Auth;

public record IssuedTokens(Guid SessionId, string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken);
public record ValidatedSession(Guid SessionId, string UserId, DeviceKind Kind);
public record DeviceSessionView(Guid Id, string Name, DeviceKind Kind, DateTimeOffset CreatedAt, DateTimeOffset LastUsedAt);

// Per-device sessions with opaque, hashed, rotating tokens. Never log raw tokens.
public class DeviceSessionService(MathomDbContext db, TimeProvider time, IOptions<DeviceAuthOptions> options)
{
    private static readonly TimeSpan LastUsedGranularity = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TokenRetention = TimeSpan.FromDays(1);

    private DeviceAuthOptions Opt => options.Value;

    public async Task<IssuedTokens> CreateAsync(
        string userId, string name, DeviceKind kind, Guid? provisionedBy, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var session = new DeviceSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            Kind = kind,
            ProvisionedBySessionId = provisionedBy,
            CreatedAt = now,
            LastUsedAt = now,
        };
        db.Set<DeviceSession>().Add(session);
        var issued = IssueTokens(session, now);
        await db.SaveChangesAsync(ct);
        return issued;
    }

    public async Task<ValidatedSession?> ValidateAccessTokenAsync(string accessToken, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var hash = Hash(accessToken);
        var token = await db.Set<DeviceToken>()
            .Include(t => t.Session)
            .FirstOrDefaultAsync(t => t.Hash == hash && t.Type == DeviceTokenType.Access, ct);
        if (token is null || token.ExpiresAt <= now || token.Session.RevokedAt is not null) return null;

        var session = token.Session;
        if (now - session.LastUsedAt > LastUsedGranularity)
        {
            session.LastUsedAt = now;
            await db.SaveChangesAsync(ct);
        }
        return new ValidatedSession(session.Id, session.UserId, session.Kind);
    }

    public async Task<(IssuedTokens Tokens, string UserId)?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var token = await FindUsableRefreshTokenAsync(refreshToken, now, ct);
        if (token is null) return null;

        var session = token.Session;
        token.SupersededAt ??= now;
        session.LastUsedAt = now;

        var issued = IssueTokens(session, now);
        await db.SaveChangesAsync(ct);

        // Set-based so concurrent refreshes cannot fail on rows another one already deleted.
        var cutoff = now - TokenRetention;
        await db.Set<DeviceToken>()
            .Where(t => t.SessionId == session.Id && t.ExpiresAt < cutoff)
            .ExecuteDeleteAsync(ct);
        return (issued, session.UserId);
    }

    // Same validity rules as RefreshAsync, without rotating. Side effect: like RefreshAsync,
    // presenting a token superseded beyond the grace window revokes the session (reuse
    // detection). The HTTP layer calls this first and stops on null, so it must detect reuse too.
    public async Task<string?> GetUserIdForRefreshTokenAsync(string refreshToken, CancellationToken ct)
    {
        var token = await FindUsableRefreshTokenAsync(refreshToken, time.GetUtcNow(), ct);
        return token?.Session.UserId;
    }

    public async Task<bool> RevokeAsync(string userId, Guid sessionId, CancellationToken ct)
    {
        var session = await db.Set<DeviceSession>()
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, ct);
        if (session is null) return false;
        session.RevokedAt ??= time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<DeviceSessionView>> ListActiveAsync(string userId, CancellationToken ct)
    {
        var idleCutoff = time.GetUtcNow() - Opt.RefreshTokenIdleLifetime;
        return await db.Set<DeviceSession>()
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.LastUsedAt > idleCutoff)
            .OrderByDescending(s => s.LastUsedAt)
            .Select(s => new DeviceSessionView(s.Id, s.Name, s.Kind, s.CreatedAt, s.LastUsedAt))
            .ToListAsync(ct);
    }

    // Returns the token when usable; null when unknown/expired/revoked. Revokes the session on reuse.
    private async Task<DeviceToken?> FindUsableRefreshTokenAsync(string refreshToken, DateTimeOffset now, CancellationToken ct)
    {
        var hash = Hash(refreshToken);
        var token = await db.Set<DeviceToken>()
            .Include(t => t.Session)
            .FirstOrDefaultAsync(t => t.Hash == hash && t.Type == DeviceTokenType.Refresh, ct);
        if (token is null || token.ExpiresAt <= now || token.Session.RevokedAt is not null) return null;

        if (token.SupersededAt is { } superseded && superseded + Opt.RefreshGraceWindow <= now)
        {
            token.Session.RevokedAt = now;
            await db.SaveChangesAsync(ct);
            return null;
        }
        return token;
    }

    private IssuedTokens IssueTokens(DeviceSession session, DateTimeOffset now)
    {
        var access = NewToken();
        var refresh = NewToken();
        var accessExpires = now + Opt.AccessTokenLifetime;
        db.Set<DeviceToken>().AddRange(
            new DeviceToken
            {
                Id = Guid.NewGuid(), SessionId = session.Id, Type = DeviceTokenType.Access,
                Hash = Hash(access), ExpiresAt = accessExpires,
            },
            new DeviceToken
            {
                Id = Guid.NewGuid(), SessionId = session.Id, Type = DeviceTokenType.Refresh,
                Hash = Hash(refresh), ExpiresAt = now + Opt.RefreshTokenIdleLifetime,
            });
        return new IssuedTokens(session.Id, access, accessExpires, refresh);
    }

    private static string NewToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string token)
        => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
}
