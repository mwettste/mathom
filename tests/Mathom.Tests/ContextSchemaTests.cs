using System;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class ContextSchemaTests(PostgresFixture fx)
{
    [Fact]
    public async Task DeletingContext_SetsItemToInbox_AndCascadesGlossary()
    {
        var u = "ctx-schema-user";
        await fx.EnsureUserAsync(u, u + "@example.com");

        var ctxId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var termId = Guid.NewGuid();

        await using (var seed = fx.NewDbContext())
        {
            seed.Contexts.Add(new Context { Id = ctxId, UserId = u, Name = "Business", CreatedAt = DateTimeOffset.UtcNow });
            seed.Items.Add(new Item
            {
                Id = itemId, Status = ItemStatus.Ready, SourceType = SourceType.Text,
                RawText = "x", CleanText = "x", IdempotencyKey = Guid.NewGuid().ToString(),
                UserId = u, ContextId = ctxId, CreatedAt = DateTimeOffset.UtcNow,
            });
            seed.GlossaryTerms.Add(new GlossaryTerm { Id = termId, UserId = u, ContextId = ctxId, Term = "Acme", CreatedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        await using (var del = fx.NewDbContext())
        {
            var ctx = await del.Contexts.SingleAsync(c => c.Id == ctxId);
            del.Contexts.Remove(ctx);
            await del.SaveChangesAsync();
        }

        await using (var verify = fx.NewDbContext())
        {
            var item = await verify.Items.SingleAsync(i => i.Id == itemId);
            Assert.Null(item.ContextId); // SET NULL → Inbox
            Assert.False(await verify.GlossaryTerms.AnyAsync(g => g.Id == termId)); // CASCADE
        }
    }
}
