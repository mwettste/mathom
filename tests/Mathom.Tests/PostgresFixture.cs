using System.Threading.Tasks;
using Mathom.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector.Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Mathom.Tests;

public class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();

    private NpgsqlDataSource _dataSource = null!;

    public string ConnectionString => _container.GetConnectionString();

    public MathomDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<MathomDbContext>()
            .UseNpgsql(_dataSource, o => o.UseVector())
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

        // Create the extension before building the vector-mapped data source (its type
        // resolution needs `vector` to exist), then migrate.
        await using (var bootstrap = new NpgsqlConnection(ConnectionString))
        {
            await bootstrap.OpenAsync();
            await using var cmd = bootstrap.CreateCommand();
            cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS vector";
            await cmd.ExecuteNonQueryAsync();
        }

        var dsb = new NpgsqlDataSourceBuilder(ConnectionString);
#pragma warning disable NPG9001
        dsb.AddTypeInfoResolverFactory(new VectorTypeInfoResolverFactory());
#pragma warning restore NPG9001
        _dataSource = dsb.Build();

        await using var db = NewDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null) await _dataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition("postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture> { }
