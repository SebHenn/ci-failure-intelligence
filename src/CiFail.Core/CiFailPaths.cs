namespace CiFail.Core;

/// <summary>
/// Resolves cifail's local data locations. Honors the <c>CIFAIL_HOME</c> environment
/// variable (useful for tests, CI, and sandboxing); otherwise defaults to
/// <c>~/.cifail</c>.
/// </summary>
public static class CiFailPaths
{
    public const string HomeEnvVar = "CIFAIL_HOME";

    /// <summary>The base directory for all cifail data (history db, user rule packs).</summary>
    public static string Home
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable(HomeEnvVar);
            if (!string.IsNullOrWhiteSpace(fromEnv))
                return fromEnv;

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cifail");
        }
    }

    /// <summary>Path to the SQLite history database.</summary>
    public static string HistoryDbPath => Path.Combine(Home, "history.db");

    /// <summary>Path to the optional YAML config file.</summary>
    public static string ConfigPath => Path.Combine(Home, "config.yaml");

    /// <summary>Directory for user-supplied rule packs (*.yaml).</summary>
    public static string UserRulesDir => Path.Combine(Home, "rules");
}
