using System;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class DbContextTests(PostgresFixture fx)
{
    private const string Uid = "dbcontext-tests-user";

    [Fact]
    public async Task CanPersistAndReadItem()
    {
        await fx.EnsureUserAsync(Uid, "dbcontext@example.com");
        await using var db = fx.NewDbContext();
        var item = Item.CreatePending(SourceType.Text, "hello world", Guid.NewGuid().ToString(), Uid, DateTimeOffset.UtcNow);
        db.Items.Add(item);
        await db.SaveChangesAsync();

        await using var db2 = fx.NewDbContext();
        var loaded = await db2.Items.SingleAsync(i => i.Id == item.Id);
        Assert.Equal("hello world", loaded.RawText);
        Assert.Equal(ItemStatus.Pending, loaded.Status);
    }

    [Fact]
    public async Task UserLanguage_PersistsAndIsUniquePerUserLocale()
    {
        var u = "ul-unique-user";
        await fx.EnsureUserAsync(u, u + "@example.com");
        await using (var db = fx.NewDbContext())
        {
            db.UserLanguages.Add(new Mathom.Web.Domain.UserLanguage
            {
                Id = System.Guid.NewGuid(), UserId = u, Locale = "en", IsPrimary = true,
                SortOrder = 0, CreatedAt = System.DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        await using (var dup = fx.NewDbContext())
        {
            dup.UserLanguages.Add(new Mathom.Web.Domain.UserLanguage
            {
                Id = System.Guid.NewGuid(), UserId = u, Locale = "en", IsPrimary = false,
                SortOrder = 1, CreatedAt = System.DateTimeOffset.UtcNow,
            });
            await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => dup.SaveChangesAsync());
        }
    }
}

[Collection("postgres")]
public class ItemTranslationDbTests(PostgresFixture fx)
{
    [Fact]
    public async Task Item_StoresSourceLanguage_AndTranslations()
    {
        var u = "it-trans-user";
        await fx.EnsureUserAsync(u, u + "@example.com");
        var id = System.Guid.NewGuid();
        await using (var db = fx.NewDbContext())
        {
            var item = Mathom.Web.Domain.Item.CreatePending(
                Mathom.Web.Domain.SourceType.Text, "raw", System.Guid.NewGuid().ToString(), u, System.DateTimeOffset.UtcNow);
            item.GetType().GetProperty("Id")!.SetValue(item, id);
            item.SourceLanguage = "de-CH";
            item.Title = "Titel"; item.CleanText = "Inhalt"; item.Status = Mathom.Web.Domain.ItemStatus.Ready;
            item.Translations.Add(new Mathom.Web.Domain.ItemTranslation
            {
                Id = System.Guid.NewGuid(), ItemId = id, Locale = "en", Title = "Title", CleanText = "Content",
            });
            db.Items.Add(item);
            await db.SaveChangesAsync();
        }
        await using (var verify = fx.NewDbContext())
        {
            var loaded = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .Include(verify.Items, i => i.Translations)
                .FirstAsync(i => i.Id == id);
            Assert.Equal("de-CH", loaded.SourceLanguage);
            Assert.Single(loaded.Translations);
            Assert.Equal("en", loaded.Translations[0].Locale);
        }
    }
}
