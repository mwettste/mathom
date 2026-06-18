using System.Threading.Tasks;
using Mathom.Web.Data;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Mathom.Tests;

public class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:17").Build();

    public string ConnectionString => _container.GetConnectionString();

    public MathomDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<MathomDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new MathomDbContext(options);
    }

    public async Task EnsureUserAsync(string id, string email)
    {
        await using var db = NewDbContext();
        if (await db.Users.FindAsync(id) is not null) return;
        db.Users.Add(new Mathom.Web.Domain.ApplicationUser
        {
            Id = id,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            SecurityStamp = id,
        });
        await db.SaveChangesAsync();
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = NewDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition("postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture> { }
