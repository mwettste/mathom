using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Glossary;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class GlossaryServiceTests(PostgresFixture fx)
{
    [Fact]
    public async Task Add_Normalizes_Dedupes_RejectsEmpty()
    {
        var u = "gloss-add-user";
        await fx.EnsureUserAsync(u, u + "@example.com");
        await using var db = fx.NewDbContext();
        var svc = new GlossaryService(db);

        Assert.True(await svc.AddAsync(u, null, "  Obersaxen  ", null, CancellationToken.None)); // trimmed
        Assert.False(await svc.AddAsync(u, null, "obersaxen", null, CancellationToken.None));    // case-insensitive dup
        Assert.False(await svc.AddAsync(u, null, "   ", null, CancellationToken.None));          // empty

        var terms = await svc.GetTermsAsync(u, null, CancellationToken.None);
        Assert.Equal(new[] { "Obersaxen" }, terms);
    }

    [Fact]
    public async Task Remove_Works_AndListIsOldestFirst()
    {
        var u = "gloss-remove-user";
        await fx.EnsureUserAsync(u, u + "@example.com");
        await using var db = fx.NewDbContext();
        var svc = new GlossaryService(db);
        await svc.AddAsync(u, null, "Alpha", null, CancellationToken.None);
        await svc.AddAsync(u, null, "Beta", null, CancellationToken.None);

        var termsBeforeRemove = await svc.GetTermsAsync(u, null, CancellationToken.None);
        Assert.Equal(new[] { "Alpha", "Beta" }, termsBeforeRemove); // oldest-first ordering

        var beta = await db.GlossaryTerms.FirstAsync(g => g.Term == "Beta");
        Assert.True(await svc.RemoveAsync(u, beta.Id, CancellationToken.None));

        var terms = await svc.GetTermsAsync(u, null, CancellationToken.None);
        Assert.Equal(new[] { "Alpha" }, terms);
    }

    [Fact]
    public async Task Glossary_IsUserScoped()
    {
        var a = "gloss-a"; var b = "gloss-b";
        await fx.EnsureUserAsync(a, a + "@example.com");
        await fx.EnsureUserAsync(b, b + "@example.com");
        await using var db = fx.NewDbContext();
        var svc = new GlossaryService(db);
        await svc.AddAsync(a, null, "ATerm", null, CancellationToken.None);
        await svc.AddAsync(b, null, "BTerm", null, CancellationToken.None);

        Assert.Equal(new[] { "ATerm" }, await svc.GetTermsAsync(a, null, CancellationToken.None));
        var aTerm = await db.GlossaryTerms.FirstAsync(g => g.Term == "ATerm");
        Assert.False(await svc.RemoveAsync(b, aTerm.Id, CancellationToken.None)); // B can't remove A's
    }

    [Fact]
    public async Task Add_CapturesVariant_WhenDifferentFromTerm()
    {
        var u = "gloss-variant-user";
        await fx.EnsureUserAsync(u, u + "@example.com");
        await using var db = fx.NewDbContext();
        var svc = new GlossaryService(db);

        Assert.True(await svc.AddAsync(u, null, "FireSkills", "Fairstills", CancellationToken.None)); // term + variant
        Assert.False(await svc.AddAsync(u, null, "FireSkills", "fairstills", CancellationToken.None)); // dup term + dup variant (ci)
        Assert.True(await svc.AddAsync(u, null, "FireSkills", "Fair Stills", CancellationToken.None));  // existing term, new variant
        Assert.False(await svc.AddAsync(u, null, "FireSkills", "FireSkills", CancellationToken.None));   // variant == term: nothing new

        var entries = await svc.GetEntriesAsync(u, null, CancellationToken.None);
        var entry = Assert.Single(entries);
        Assert.Equal("FireSkills", entry.Term);
        Assert.Equal(new[] { "Fair Stills", "Fairstills" }, entry.Variants.OrderBy(v => v).ToArray());
    }

    [Fact]
    public async Task SetDescription_Set_Clear_Truncate_AndUserScoped()
    {
        var owner = "desc-owner";
        var attacker = "desc-attacker";
        await fx.EnsureUserAsync(owner, owner + "@example.com");
        await fx.EnsureUserAsync(attacker, attacker + "@example.com");
        await using var db = fx.NewDbContext();
        var svc = new GlossaryService(db);
        await svc.AddAsync(owner, null, "FireSkills", null, CancellationToken.None);
        var termId = (await svc.GetTermViewsAsync(owner, null, CancellationToken.None)).Single().Id;

        // Set
        Assert.True(await svc.SetDescriptionAsync(owner, termId, "  our internal time-tracking product  ", CancellationToken.None));
        Assert.Equal("our internal time-tracking product",
            (await svc.GetEntriesAsync(owner, null, CancellationToken.None)).Single().Description);

        // Truncate to 500
        Assert.True(await svc.SetDescriptionAsync(owner, termId, new string('x', 700), CancellationToken.None));
        Assert.Equal(500, (await svc.GetEntriesAsync(owner, null, CancellationToken.None)).Single().Description!.Length);

        // Clear (whitespace -> null)
        Assert.True(await svc.SetDescriptionAsync(owner, termId, "   ", CancellationToken.None));
        Assert.Null((await svc.GetTermViewsAsync(owner, null, CancellationToken.None)).Single().Description);

        // Cross-user: attacker cannot set the owner's term
        Assert.False(await svc.SetDescriptionAsync(attacker, termId, "hacked", CancellationToken.None));
        Assert.Null((await svc.GetTermViewsAsync(owner, null, CancellationToken.None)).Single().Description);
    }

    [Fact]
    public async Task Glossary_IsContextScoped()
    {
        var u = "gloss-ctx-user";
        await fx.EnsureUserAsync(u, u + "@example.com");
        var ctxId = Guid.NewGuid();
        await using var db = fx.NewDbContext();
        db.Contexts.Add(new Mathom.Web.Domain.Context { Id = ctxId, UserId = u, Name = "Biz", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var svc = new GlossaryService(db);

        Assert.True(await svc.AddAsync(u, ctxId, "Acme", null, CancellationToken.None));
        Assert.True(await svc.AddAsync(u, null, "Family", null, CancellationToken.None));

        Assert.Equal(new[] { "Acme" }, await svc.GetTermsAsync(u, ctxId, CancellationToken.None));
        Assert.Equal(new[] { "Family" }, await svc.GetTermsAsync(u, null, CancellationToken.None));
    }

    [Fact]
    public async Task RemoveVariant_IsUserScoped_AndCascadesOnTermRemove()
    {
        var owner = "gv-owner";
        var attacker = "gv-attacker";
        await fx.EnsureUserAsync(owner, owner + "@example.com");
        await fx.EnsureUserAsync(attacker, attacker + "@example.com");
        await using var db = fx.NewDbContext();
        var svc = new GlossaryService(db);
        await svc.AddAsync(owner, null, "FireSkills", "Fairstills", CancellationToken.None);

        var view = Assert.Single(await svc.GetTermViewsAsync(owner, null, CancellationToken.None));
        var variantId = Assert.Single(view.Variants).Id;

        Assert.False(await svc.RemoveVariantAsync(attacker, variantId, CancellationToken.None)); // cross-user
        Assert.True(await svc.RemoveVariantAsync(owner, variantId, CancellationToken.None));
        Assert.Empty((await svc.GetTermViewsAsync(owner, null, CancellationToken.None)).Single().Variants);

        // Re-add a variant, then remove the term → variant cascades.
        await svc.AddAsync(owner, null, "FireSkills", "Fairstills", CancellationToken.None);
        var termId = (await svc.GetTermViewsAsync(owner, null, CancellationToken.None)).Single().Id;
        await svc.RemoveAsync(owner, termId, CancellationToken.None);
        await using var verify = fx.NewDbContext();
        var ownerTermIds = verify.GlossaryTerms.Where(t => t.UserId == owner).Select(t => t.Id);
        Assert.Empty(await verify.GlossaryVariants.Where(v => ownerTermIds.Contains(v.GlossaryTermId)).ToListAsync());
    }
}
