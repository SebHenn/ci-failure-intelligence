using CiFail.Core.Configuration;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Configuration;

public class ConfigLoaderTests
{
    [Fact]
    public void Defaults_to_sqlite_when_nothing_is_configured()
    {
        var config = ConfigLoader.Load(configPath: "does-not-exist.yaml");
        config.Database.Provider.Should().Be("sqlite");
        config.Database.ConnectionString.Should().BeNull();
    }

    [Fact]
    public void Reads_provider_and_connection_from_yaml_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cifail-cfg-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, """
            database:
              provider: Postgres
              connectionString: "Host=db;Username=ci;Database=cifail"
            """);
        try
        {
            var config = ConfigLoader.Load(configPath: path);
            config.Database.Provider.Should().Be("postgres"); // normalized to lowercase
            config.Database.ConnectionString.Should().Contain("Host=db");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Cli_overrides_win_over_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cifail-cfg-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, "database:\n  provider: mysql\n");
        try
        {
            var config = ConfigLoader.Load(cliProvider: "sqlserver", cliConnection: "Server=x", configPath: path);
            config.Database.Provider.Should().Be("sqlserver");
            config.Database.ConnectionString.Should().Be("Server=x");
        }
        finally { File.Delete(path); }
    }
}
