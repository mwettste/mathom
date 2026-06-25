// tests/Mathom.Tests/UserLanguageServiceTests.cs
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Languages;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class UserLanguageServiceTests(PostgresFixture fx)
{
    private static UserLanguageService Svc(PostgresFixture fx, out Mathom.Web.Data.MathomDbContext db)
    {
        db = fx.NewDbContext();
        return new UserLanguageService(db);
    }

    [Fact]
    public async Task FirstAdded_BecomesPrimary_AndDefaultsAreSafeWhenEmpty()
    {
        var u = "ul-first-user";
        await fx.EnsureUserAsync(u, u + "@example.com");

        await using (var db = fx.NewDbContext())
        {
            var svc = new UserLanguageService(db);
            Assert.Empty(await svc.GetActiveLocalesAsync(u, CancellationToken.None));
            Assert.Equal("en", await svc.GetPrimaryLocaleAsync(u, CancellationToken.None));

            Assert.True(await svc.AddAsync(u, "de-CH", CancellationToken.None));
            Assert.True(await svc.AddAsync(u, "en", CancellationToken.None));
        }
        await using (var verify = fx.NewDbContext())
        {
            var svc = new UserLanguageService(verify);
            Assert.Equal("de-CH", await svc.GetPrimaryLocaleAsync(u, CancellationToken.None));
            // primary first, then by SortOrder
            Assert.Equal(new[] { "de-CH", "en" }, (await svc.GetActiveLocalesAsync(u, CancellationToken.None)).ToArray());
        }
    }

    [Fact]
    public async Task Add_RejectsUnknownLocale()
    {
        var u = "ul-bad-user";
        await fx.EnsureUserAsync(u, u + "@example.com");
        await using var db = fx.NewDbContext();
        var svc = new UserLanguageService(db);
        Assert.False(await svc.AddAsync(u, "klingon", CancellationToken.None));
        Assert.Empty(await svc.GetActiveLocalesAsync(u, CancellationToken.None));
    }

    [Fact]
    public async Task SetPrimary_MovesTheFlag_ToExactlyOne()
    {
        var u = "ul-setprimary-user";
        await fx.EnsureUserAsync(u, u + "@example.com");
        await using (var db = fx.NewDbContext())
        {
            var svc = new UserLanguageService(db);
            await svc.AddAsync(u, "en", CancellationToken.None);
            await svc.AddAsync(u, "de-DE", CancellationToken.None);
            Assert.True(await svc.SetPrimaryAsync(u, "de-DE", CancellationToken.None));
        }
        await using (var verify = fx.NewDbContext())
        {
            var svc = new UserLanguageService(verify);
            var views = await svc.GetViewsAsync(u, CancellationToken.None);
            Assert.Single(views.Where(v => v.IsPrimary));
            Assert.Equal("de-DE", views.Single(v => v.IsPrimary).Locale);
        }
    }

    [Fact]
    public async Task Remove_Primary_PromotesNext()
    {
        var u = "ul-remove-user";
        await fx.EnsureUserAsync(u, u + "@example.com");
        await using (var db = fx.NewDbContext())
        {
            var svc = new UserLanguageService(db);
            await svc.AddAsync(u, "en", CancellationToken.None);     // primary
            await svc.AddAsync(u, "fr-FR", CancellationToken.None);
            Assert.True(await svc.RemoveAsync(u, "en", CancellationToken.None));
        }
        await using (var verify = fx.NewDbContext())
        {
            var svc = new UserLanguageService(verify);
            Assert.Equal("fr-FR", await svc.GetPrimaryLocaleAsync(u, CancellationToken.None));
        }
    }
}
