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

    [Fact]
    public void Reads_embeddings_settings_from_yaml()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cifail-cfg-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, """
            ai:
              embeddings: true
              embeddingModel: nomic-embed-text
              embeddingDimensions: 384
            """);
        try
        {
            var config = ConfigLoader.Load(configPath: path);
            config.Ai.Embeddings.Should().BeTrue();
            config.Ai.EmbeddingModel.Should().Be("nomic-embed-text");
            config.Ai.EmbeddingDimensions.Should().Be(384);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Embeddings_env_vars_override_the_file()
    {
        Environment.SetEnvironmentVariable(ConfigLoader.AiEmbeddingsEnvVar, "1");
        Environment.SetEnvironmentVariable(ConfigLoader.AiEmbeddingDimsEnvVar, "1536");
        try
        {
            var config = ConfigLoader.Load(configPath: "does-not-exist.yaml");
            config.Ai.Embeddings.Should().BeTrue();
            config.Ai.EmbeddingDimensions.Should().Be(1536);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConfigLoader.AiEmbeddingsEnvVar, null);
            Environment.SetEnvironmentVariable(ConfigLoader.AiEmbeddingDimsEnvVar, null);
        }
    }
}
