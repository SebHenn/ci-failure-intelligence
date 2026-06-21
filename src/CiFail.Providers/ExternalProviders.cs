using CiFail.Core.Storage;
using CiFail.Providers.Mongo;

namespace CiFail.Providers;

/// <summary>
/// Registers every external database provider in the <see cref="StoreRegistry"/>. Called
/// once at startup — but only by builds compiled with the <c>CIFAIL_EXTERNAL_DB</c> symbol
/// (the Docker image / full build), so the slim default binary never loads these drivers.
/// </summary>
public static class ExternalProviders
{
    public static void RegisterAll()
    {
        StoreRegistry.Register(new PostgresStoreProvider());
        StoreRegistry.Register(new PgVectorStoreProvider());
        StoreRegistry.Register(new MySqlStoreProvider());
        StoreRegistry.Register(new SqlServerStoreProvider());
        StoreRegistry.Register(new MongoStoreProvider());
    }
}
