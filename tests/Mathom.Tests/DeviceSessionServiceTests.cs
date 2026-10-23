using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Auth;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class DeviceSessionServiceTests(PostgresFixture fx)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private async Task<(DeviceSessionService Svc, FakeTimeProvider Time, MathomDbContext Db, string User)> SetupAsync(string user)
    {
        await fx.EnsureUserAsync(user, user + "@example.com");
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var db = fx.NewDbContext();
        return (new DeviceSessionService(db, time, Options.Create(new DeviceAuthOptions())), time, db, user);
    }

    [Fact]
    public async Task Create_ThenValidateAccessToken_ReturnsSession()
    {
        var (svc, _, db, u) = await SetupAsync("dev-create");
        await using var _ = db;
        var t = await svc.CreateAsync(u, "iPhone", DeviceKind.Phone, null, Ct);
        var v = await svc.ValidateAccessTokenAsync(t.AccessToken, Ct);
        Assert.Equal(new ValidatedSession(t.SessionId, u, DeviceKind.Phone), v);
    }

    [Fact]
    public async Task Create_StoresOnlyHashes()
    {
        var (svc, _, db, u) = await SetupAsync("dev-hash");
        await using var _ = db;
        var t = await svc.CreateAsync(u, "iPhone", DeviceKind.Phone, null, Ct);
        var rows = await db.Set<DeviceToken>().Where(x => x.SessionId == t.SessionId).ToListAsync(Ct);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(64, r.Hash.Length));
        Assert.DoesNotContain(rows, r => r.Hash == t.AccessToken || r.Hash == t.RefreshToken);
    }

    [Fact]
    public async Task AccessToken_Expires_After15Minutes()
    {
        var (svc, time, db, u) = await SetupAsync("dev-expire");
        await using var _ = db;
        var t = await svc.CreateAsync(u, "iPhone", DeviceKind.Phone, null, Ct);
        time.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        Assert.Null(await svc.ValidateAccessTokenAsync(t.AccessToken, Ct));
    }

    [Fact]
    public async Task Refresh_IssuesNewPair_OldAccessTokenStillValidUntilExpiry()
    {
        var (svc, time, db, u) = await SetupAsync("dev-refresh");
        await using var _ = db;
        var t = await svc.CreateAsync(u, "iPhone", DeviceKind.Phone, null, Ct);
        time.Advance(TimeSpan.FromMinutes(1));
        var r = await svc.RefreshAsync(t.RefreshToken, Ct);
        Assert.NotNull(r);
        Assert.Equal(u, r!.Value.UserId);
        Assert.Equal(t.SessionId, r.Value.Tokens.SessionId);
        Assert.NotEqual(t.RefreshToken, r.Value.Tokens.RefreshToken);
        Assert.NotNull(await svc.ValidateAccessTokenAsync(t.AccessToken, Ct));
        Assert.NotNull(await svc.ValidateAccessTokenAsync(r.Value.Tokens.AccessToken, Ct));
        time.Advance(TimeSpan.FromMinutes(15));
        Assert.Null(await svc.ValidateAccessTokenAsync(t.AccessToken, Ct));
    }

    [Fact]
    public async Task Refresh_WithSupersededToken_InsideGraceWindow_IssuesAnotherWorkingPair()
    {
        var (svc, time, db, u) = await SetupAsync("dev-grace");
        await using var _ = db;
        var t = await svc.CreateAsync(u, "iPhone", DeviceKind.Phone, null, Ct);
        var first = await svc.RefreshAsync(t.RefreshToken, Ct);
        time.Advance(TimeSpan.FromMinutes(1));
        var second = await svc.RefreshAsync(t.RefreshToken, Ct);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(await svc.ValidateAccessTokenAsync(first!.Value.Tokens.AccessToken, Ct));
        Assert.NotNull(await svc.ValidateAccessTokenAsync(second!.Value.Tokens.AccessToken, Ct));
    }

    [Fact]
    public async Task Refresh_WithSupersededToken_AfterGraceWindow_RevokesSession()
    {
        var (svc, time, db, u) = await SetupAsync("dev-reuse");
        await using var _ = db;
        var t = await svc.CreateAsync(u, "iPhone", DeviceKind.Phone, null, Ct);
        var first = await svc.RefreshAsync(t.RefreshToken, Ct);
        time.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        Assert.Null(await svc.RefreshAsync(t.RefreshToken, Ct));
        Assert.Null(await svc.ValidateAccessTokenAsync(first!.Value.Tokens.AccessToken, Ct));
        Assert.Null(await svc.RefreshAsync(first.Value.Tokens.RefreshToken, Ct));
    }

    [Fact]
    public async Task GetUserIdForRefreshToken_WithSupersededTokenAfterGrace_RevokesSession()
    {
        var (svc, time, db, u) = await SetupAsync("dev-reuse-get");
        await using var _ = db;
        var t = await svc.CreateAsync(u, "iPhone", DeviceKind.Phone, null, Ct);
        var first = await svc.RefreshAsync(t.RefreshToken, Ct);
        Assert.Equal(u, await svc.GetUserIdForRefreshTokenAsync(t.RefreshToken, Ct));
        time.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        Assert.Null(await svc.GetUserIdForRefreshTokenAsync(t.RefreshToken, Ct));
        Assert.Null(await svc.ValidateAccessTokenAsync(first!.Value.Tokens.AccessToken, Ct));
        Assert.Null(await svc.GetUserIdForRefreshTokenAsync(first.Value.Tokens.RefreshToken, Ct));
    }

    [Fact]
    public async Task Refresh_AfterIdleLifetime_ReturnsNull()
    {
        var (svc, time, db, u) = await SetupAsync("dev-idle");
        await using var _ = db;
        var t = await svc.CreateAsync(u, "iPhone", DeviceKind.Phone, null, Ct);
        time.Advance(TimeSpan.FromDays(90) + TimeSpan.FromSeconds(1));
        Assert.Null(await svc.RefreshAsync(t.RefreshToken, Ct));
        Assert.Null(await svc.GetUserIdForRefreshTokenAsync(t.RefreshToken, Ct));
    }

    [Fact]
    public async Task Revoke_KillsAccessAndRefresh_EvenInGraceWindow()
    {
        var (svc, _, db, u) = await SetupAsync("dev-revoke");
        await using var _ = db;
        var t = await svc.CreateAsync(u, "iPhone", DeviceKind.Phone, null, Ct);
        var r = await svc.RefreshAsync(t.RefreshToken, Ct); // t.RefreshToken now superseded, in grace
        Assert.True(await svc.RevokeAsync(u, t.SessionId, Ct));
        Assert.Null(await svc.ValidateAccessTokenAsync(t.AccessToken, Ct));
        Assert.Null(await svc.ValidateAccessTokenAsync(r!.Value.Tokens.AccessToken, Ct));
        Assert.Null(await svc.RefreshAsync(t.RefreshToken, Ct));
        Assert.Null(await svc.RefreshAsync(r.Value.Tokens.RefreshToken, Ct));
    }

    [Fact]
    public async Task Revoke_IsUserScoped()
    {
        var (svc, _, db, u) = await SetupAsync("dev-scope-a");
        await using var _ = db;
        await fx.EnsureUserAsync("dev-scope-b", "dev-scope-b@example.com");
        var t = await svc.CreateAsync(u, "iPhone", DeviceKind.Phone, null, Ct);
        Assert.False(await svc.RevokeAsync("dev-scope-b", t.SessionId, Ct));
        Assert.NotNull(await svc.ValidateAccessTokenAsync(t.AccessToken, Ct));
    }

    [Fact]
    public async Task ListActive_ExcludesRevoked_AndOtherUsers()
    {
        var (svc, _, db, u) = await SetupAsync("dev-list-a");
        await using var _ = db;
        await fx.EnsureUserAsync("dev-list-b", "dev-list-b@example.com");
        var keep = await svc.CreateAsync(u, "Keep", DeviceKind.Phone, null, Ct);
        var gone = await svc.CreateAsync(u, "Gone", DeviceKind.Watch, null, Ct);
        await svc.CreateAsync("dev-list-b", "Other", DeviceKind.Phone, null, Ct);
        await svc.RevokeAsync(u, gone.SessionId, Ct);
        var list = await svc.ListActiveAsync(u, Ct);
        var only = Assert.Single(list);
        Assert.Equal(keep.SessionId, only.Id);
        Assert.Equal("Keep", only.Name);
    }
}
