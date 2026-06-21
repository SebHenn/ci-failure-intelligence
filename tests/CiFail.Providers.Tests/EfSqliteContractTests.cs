using CiFail.Providers.Ef;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CiFail.Providers.Tests;

/// <summary>
/// Runs the shared <see cref="StoreContract"/> against <see cref="EfAnalysisStore"/> using
/// EF Core's SQLite provider over an in-memory connection. This needs no Docker, so the EF
/// store's logic (schema creation, round-trips, resolutions) is validated on every build —
/// the Postgres/MySQL/SQL Server tests exercise the same code against the real engines in CI.
/// </summary>
public class EfSqliteContractTests
{
    [Fact]
    public void Ef_store_satisfies_the_store_contract()
    {
        // An in-memory SQLite database lives only as long as its connection is open, so we
        // own the connection and keep it open for the store's lifetime.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<CiFailDbContext>()
            .UseSqlite(connection)
            .Options;

        using var store = new EfAnalysisStore(new CiFailDbContext(options));
        StoreContract.Verify(store);
    }
}
