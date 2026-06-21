using CiFail.Core.Storage;
using CiFail.Providers;
using CiFail.Providers.Mongo;
using Testcontainers.MongoDb;
using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

namespace CiFail.Providers.Tests;

/// <summary>
/// Holds every external provider to the same <see cref="StoreContract"/> as the local
/// EF-SQLite test, but against the real database engines spun up in throwaway containers.
/// Gated by <see cref="IntegrationGate"/> (CIFAIL_DB_IT=1) so they only run where Docker
/// is available — CI's Linux runner. The store impls are obtained through the public
/// provider classes, exercising the exact path the CLI uses.
/// </summary>
public class RealEngineContractTests
{
    [SkippableFact]
    public async Task Postgres_satisfies_the_store_contract()
    {
        Skip.IfNot(IntegrationGate.Enabled, IntegrationGate.SkipReason);

        await using var container = new PostgreSqlBuilder().Build();
        await container.StartAsync();

        using var store = new PostgresStoreProvider().Create(container.GetConnectionString());
        StoreContract.Verify(store);
    }

    [SkippableFact]
    public async Task MySql_satisfies_the_store_contract()
    {
        Skip.IfNot(IntegrationGate.Enabled, IntegrationGate.SkipReason);

        await using var container = new MySqlBuilder().Build();
        await container.StartAsync();

        using var store = new MySqlStoreProvider().Create(container.GetConnectionString());
        StoreContract.Verify(store);
    }

    [SkippableFact]
    public async Task SqlServer_satisfies_the_store_contract()
    {
        Skip.IfNot(IntegrationGate.Enabled, IntegrationGate.SkipReason);

        await using var container = new MsSqlBuilder().Build();
        await container.StartAsync();

        using var store = new SqlServerStoreProvider().Create(container.GetConnectionString());
        StoreContract.Verify(store);
    }

    [SkippableFact]
    public async Task MongoDb_satisfies_the_store_contract()
    {
        Skip.IfNot(IntegrationGate.Enabled, IntegrationGate.SkipReason);

        await using var container = new MongoDbBuilder().Build();
        await container.StartAsync();

        using var store = new MongoStoreProvider().Create(container.GetConnectionString());
        StoreContract.Verify(store);
    }
}
