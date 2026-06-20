using CiFail.Core.Configuration;
using CiFail.Core.Storage;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Storage;

public class StoreFactoryTests
{
    [Fact]
    public void Sqlite_is_always_available_as_the_built_in_default()
    {
        StoreRegistry.AvailableNames.Should().Contain("sqlite");

        var dbPath = Path.Combine(Path.GetTempPath(), $"cifail-factory-{Guid.NewGuid():N}.db");
        using (var store = StoreFactory.Create(new DatabaseConfig { Provider = "sqlite", ConnectionString = dbPath }))
        {
            store.Should().BeOfType<SqliteAnalysisRepository>();
        }
        File.Delete(dbPath);
    }

    [Fact]
    public void Unknown_or_unbundled_provider_throws_a_clear_error()
    {
        var act = () => StoreFactory.Create(new DatabaseConfig { Provider = "postgres" });

        act.Should().Throw<StoreProviderNotAvailableException>()
            .Which.Message.Should().Contain("postgres").And.Contain("not available");
    }
}
