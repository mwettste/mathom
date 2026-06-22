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

        Assert.True(await svc.AddAsync(u, "  Obersaxen  ", null, CancellationToken.None)); // trimmed
        Assert.False(await svc.AddAsync(u, "obersaxen", null, CancellationToken.None));    // case-insensitive dup
        Assert.False(await svc.AddAsync(u, "   ", null, CancellationToken.None));          // empty

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
        await svc.AddAsync(u, "Alpha", null, CancellationToken.None);
        await svc.AddAsync(u, "Beta", null, CancellationToken.None);

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
        await svc.AddAsync(a, "ATerm", null, CancellationToken.None);
        await svc.AddAsync(b, "BTerm", null, CancellationToken.None);

        Assert.Equal(new[] { "ATerm" }, await svc.GetTermsAsync(a, CancellationToken.None));
        var aTerm = await db.GlossaryTerms.FirstAsync(g => g.Term == "ATerm");
        Assert.False(await svc.RemoveAsync(b, aTerm.Id, CancellationToken.None)); // B can't remove A's
    }

    [Fact]
    public async Task Add_CapturesVariant_WhenDifferentFromTerm()
    {
        var u = "gloss-variant-user";
        await _fx.EnsureUserAsync(u, u + "@example.com");
        await using var db = _fx.NewDbContext();
        var svc = new GlossaryService(db);

        Assert.True(await svc.AddAsync(u, "FireSkills", "Fairstills", CancellationToken.None)); // term + variant
        Assert.False(await svc.AddAsync(u, "FireSkills", "fairstills", CancellationToken.None)); // dup term + dup variant (ci)
        Assert.True(await svc.AddAsync(u, "FireSkills", "Fair Stills", CancellationToken.None));  // existing term, new variant
        Assert.False(await svc.AddAsync(u, "FireSkills", "FireSkills", CancellationToken.None));   // variant == term: nothing new

        var entries = await svc.GetEntriesAsync(u, CancellationToken.None);
        var entry = Assert.Single(entries);
        Assert.Equal("FireSkills", entry.Term);
        Assert.Equal(new[] { "Fair Stills", "Fairstills" }, entry.Variants.OrderBy(v => v).ToArray());
    }

    [Fact]
    public async Task RemoveVariant_IsUserScoped_AndCascadesOnTermRemove()
    {
        var owner = "gv-owner";
        var attacker = "gv-attacker";
        await _fx.EnsureUserAsync(owner, owner + "@example.com");
        await _fx.EnsureUserAsync(attacker, attacker + "@example.com");
        await using var db = _fx.NewDbContext();
        var svc = new GlossaryService(db);
        await svc.AddAsync(owner, "FireSkills", "Fairstills", CancellationToken.None);

        var view = Assert.Single(await svc.GetTermViewsAsync(owner, CancellationToken.None));
        var variantId = Assert.Single(view.Variants).Id;

        Assert.False(await svc.RemoveVariantAsync(attacker, variantId, CancellationToken.None)); // cross-user
        Assert.True(await svc.RemoveVariantAsync(owner, variantId, CancellationToken.None));
        Assert.Empty((await svc.GetTermViewsAsync(owner, CancellationToken.None)).Single().Variants);

        // Re-add a variant, then remove the term → variant cascades.
        await svc.AddAsync(owner, "FireSkills", "Fairstills", CancellationToken.None);
        var termId = (await svc.GetTermViewsAsync(owner, CancellationToken.None)).Single().Id;
        await svc.RemoveAsync(owner, termId, CancellationToken.None);
        await using var verify = _fx.NewDbContext();
        var ownerTermIds = verify.GlossaryTerms.Where(t => t.UserId == owner).Select(t => t.Id);
        Assert.Empty(await verify.GlossaryVariants.Where(v => ownerTermIds.Contains(v.GlossaryTermId)).ToListAsync());
    }
}
