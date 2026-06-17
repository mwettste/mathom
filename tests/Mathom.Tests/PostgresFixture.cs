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
