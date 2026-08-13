using CiFail.Core.Configuration;

namespace CiFail.Core.Rules;

/// <summary>
/// Where cifail looks for user rule packs, in the order it loads them.
///
/// <para>
/// Custom rules are most often <b>repository</b> rules: "two runs of the same seed produced
/// different bytes" means nothing anywhere else and belongs next to the workflow that can
/// trigger it, versioned and reviewed with it. Before this there was only
/// <c>$CIFAIL_HOME/rules</c>, so shipping a pack with a repo meant repointing
/// <c>CIFAIL_HOME</c> at the checkout — which also moves <c>history.db</c>, so the same
/// machine then had a different history depending on which directory you ran from.
/// </para>
///
/// <para>
/// Later entries win: a pack found further down this list overrides an earlier rule with the
/// same id, so the order runs from the most general location to the most explicit one.
/// </para>
/// </summary>
public static class RuleSearchPath
{
    /// <summary>Folder inside a repository's <c>.cifail</c> directory that holds its packs.</summary>
    public const string RepoRulesFolder = "rules";

    /// <summary>
    /// The directories to load packs from, most general first. Entries are absolute and
    /// de-duplicated; directories that don't exist are kept, so <c>cifail config</c> can show
    /// a configured path that isn't there (the usual reason a rule "didn't load").
    /// </summary>
    /// <param name="extraDirs">Directories from the command line (<c>--rules</c>), highest precedence.</param>
    /// <param name="config">Pre-loaded config; omit to read the file + <c>CIFAIL_RULES</c>.</param>
    /// <param name="workingDirectory">Where to start looking for a repo pack; defaults to the current directory.</param>
    public static IReadOnlyList<string> Resolve(
        IEnumerable<string>? extraDirs = null,
        CiFailConfig? config = null,
        string? workingDirectory = null)
    {
        config ??= SafeLoad();

        var dirs = new List<string>();
        var seen = new HashSet<string>(PathComparer);

        // 1. This machine's own packs.
        Add(CiFailPaths.UserRulesDir);

        // 2. The repository's packs, if the working directory is inside one.
        if (DiscoverRepoRules(workingDirectory ?? Directory.GetCurrentDirectory()) is { } repo)
            Add(repo);

        // 3. config.yaml `rules.paths`, then CIFAIL_RULES (the loader appends env after file).
        foreach (var path in config.Rules.Paths)
            Add(path);

        // 4. --rules on the command line.
        foreach (var path in extraDirs ?? Enumerable.Empty<string>())
            Add(path);

        return dirs;

        void Add(string path)
        {
            if (Normalize(path) is { } full && seen.Add(full)) dirs.Add(full);
        }
    }

    /// <summary>
    /// The nearest <c>.cifail/rules</c> at or above <paramref name="startDirectory"/>, or null.
    /// Walking up means it still works from a subdirectory, the way a tool finds its own config —
    /// and it makes a repo pack a sibling of the gate baseline that already lives in
    /// <c>.cifail/</c>, with no configuration at all.
    /// </summary>
    public static string? DiscoverRepoRules(string startDirectory)
    {
        DirectoryInfo? dir;
        try
        {
            dir = new DirectoryInfo(startDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return null;
        }

        for (; dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, CiFailPaths.RepoDirectoryName, RepoRulesFolder);
            if (Directory.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>Split a <c>PATH</c>-style list of directories (<c>;</c> on Windows, <c>:</c> elsewhere).</summary>
    public static IEnumerable<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Enumerable.Empty<string>()
            : value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// A bad config file must not cost you your rules — <c>cifail config</c> is where a broken
    /// file gets reported, loudly and in one place.
    /// </summary>
    private static CiFailConfig SafeLoad()
    {
        try
        {
            return ConfigLoader.Load();
        }
        catch (ConfigException)
        {
            return new CiFailConfig();
        }
    }

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Absolute, separator-normalized form of a configured path, or null if it isn't one.</summary>
    private static string? Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
