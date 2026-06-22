using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Glossary;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class GlossaryServiceTests
{
    private readonly PostgresFixture _fx;
    public GlossaryServiceTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task Add_Normalizes_Dedupes_RejectsEmpty()
    {
        var u = "gloss-add-user";
        await _fx.EnsureUserAsync(u, u + "@example.com");
        await using var db = _fx.NewDbContext();
        var svc = new GlossaryService(db);

        Assert.True(await svc.AddAsync(u, "  Obersaxen  ", CancellationToken.None)); // trimmed
        Assert.False(await svc.AddAsync(u, "obersaxen", CancellationToken.None));    // case-insensitive dup
        Assert.False(await svc.AddAsync(u, "   ", CancellationToken.None));          // empty

        var terms = await svc.GetTermsAsync(u, CancellationToken.None);
        Assert.Equal(new[] { "Obersaxen" }, terms);
    }

    [Fact]
    public async Task Remove_Works_AndListIsOldestFirst()
    {
        var u = "gloss-remove-user";
        await _fx.EnsureUserAsync(u, u + "@example.com");
        await using var db = _fx.NewDbContext();
        var svc = new GlossaryService(db);
        await svc.AddAsync(u, "Alpha", CancellationToken.None);
        await svc.AddAsync(u, "Beta", CancellationToken.None);

        var termsBeforeRemove = await svc.GetTermsAsync(u, CancellationToken.None);
        Assert.Equal(new[] { "Alpha", "Beta" }, termsBeforeRemove); // oldest-first ordering

        var beta = await db.GlossaryTerms.FirstAsync(g => g.Term == "Beta");
        Assert.True(await svc.RemoveAsync(u, beta.Id, CancellationToken.None));

        var terms = await svc.GetTermsAsync(u, CancellationToken.None);
        Assert.Equal(new[] { "Alpha" }, terms);
    }

    [Fact]
    public async Task Glossary_IsUserScoped()
    {
        var a = "gloss-a"; var b = "gloss-b";
        await _fx.EnsureUserAsync(a, a + "@example.com");
        await _fx.EnsureUserAsync(b, b + "@example.com");
        await using var db = _fx.NewDbContext();
        var svc = new GlossaryService(db);
        await svc.AddAsync(a, "ATerm", CancellationToken.None);
        await svc.AddAsync(b, "BTerm", CancellationToken.None);

        Assert.Equal(new[] { "ATerm" }, await svc.GetTermsAsync(a, CancellationToken.None));
        var aTerm = await db.GlossaryTerms.FirstAsync(g => g.Term == "ATerm");
        Assert.False(await svc.RemoveAsync(b, aTerm.Id, CancellationToken.None)); // B can't remove A's
    }
}
