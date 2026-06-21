using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CiFail.Core.Configuration;

/// <summary>
/// Builds the effective <see cref="CiFailConfig"/> by layering sources, highest
/// precedence last: defaults → config file (<c>~/.cifail/config.yaml</c>) → environment
/// (<c>CIFAIL_DB_PROVIDER</c>, <c>CIFAIL_DB_CONNECTION</c>) → explicit CLI overrides.
/// </summary>
public static class ConfigLoader
{
    public const string ProviderEnvVar = "CIFAIL_DB_PROVIDER";
    public const string ConnectionEnvVar = "CIFAIL_DB_CONNECTION";
    public const string AiProviderEnvVar = "CIFAIL_AI_PROVIDER";
    public const string AiModelEnvVar = "CIFAIL_AI_MODEL";
    public const string AiUrlEnvVar = "CIFAIL_AI_URL";

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Load config from the file (if present), then apply env and CLI overrides.
    /// CLI values win over env, which win over the file, which wins over defaults.
    /// </summary>
    public static CiFailConfig Load(
        string? cliProvider = null,
        string? cliConnection = null,
        string? configPath = null)
    {
        var config = LoadFromFile(configPath ?? CiFailPaths.ConfigPath);

        var envProvider = Environment.GetEnvironmentVariable(ProviderEnvVar);
        var envConnection = Environment.GetEnvironmentVariable(ConnectionEnvVar);

        if (!string.IsNullOrWhiteSpace(envProvider)) config.Database.Provider = envProvider.Trim();
        if (!string.IsNullOrWhiteSpace(envConnection)) config.Database.ConnectionString = envConnection;

        if (!string.IsNullOrWhiteSpace(cliProvider)) config.Database.Provider = cliProvider.Trim();
        if (!string.IsNullOrWhiteSpace(cliConnection)) config.Database.ConnectionString = cliConnection;

        config.Database.Provider = config.Database.Provider.ToLowerInvariant();

        // AI: file → env (CLI overrides are applied by the caller before AiFactory.Create).
        var aiProvider = Environment.GetEnvironmentVariable(AiProviderEnvVar);
        var aiModel = Environment.GetEnvironmentVariable(AiModelEnvVar);
        var aiUrl = Environment.GetEnvironmentVariable(AiUrlEnvVar);
        if (!string.IsNullOrWhiteSpace(aiProvider)) config.Ai.Provider = aiProvider.Trim();
        if (!string.IsNullOrWhiteSpace(aiModel)) config.Ai.Model = aiModel;
        if (!string.IsNullOrWhiteSpace(aiUrl)) config.Ai.BaseUrl = aiUrl;
        config.Ai.Provider = config.Ai.Provider.ToLowerInvariant();

        return config;
    }

    private static CiFailConfig LoadFromFile(string path)
    {
        if (!File.Exists(path)) return new CiFailConfig();
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return new CiFailConfig();
        return Yaml.Deserialize<CiFailConfig>(text) ?? new CiFailConfig();
    }
}
