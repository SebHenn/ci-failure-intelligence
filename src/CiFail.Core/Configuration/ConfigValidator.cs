using System.Reflection;
using CiFail.Core.Ai;
using CiFail.Core.Notifications;
using CiFail.Core.Rules;
using CiFail.Core.Storage;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace CiFail.Core.Configuration;

/// <summary>One problem found in <c>config.yaml</c>, located by its dotted setting path.</summary>
/// <param name="Severity">Errors mean the setting can't work; warnings mean it's suspicious.</param>
/// <param name="Path">Dotted path to the setting, e.g. <c>notifications.slackWebhookUrl</c>.</param>
/// <param name="Message">What's wrong, in plain words.</param>
/// <param name="Line">1-based line in the file, when known.</param>
public sealed record ConfigDiagnostic(DiagnosticSeverity Severity, string Path, string Message, int? Line = null);

/// <summary>
/// Lints <c>~/.cifail/config.yaml</c> and reports what the loader silently tolerates.
///
/// <para>
/// The loader keeps <c>IgnoreUnmatchedProperties()</c> on purpose — an old cifail must not
/// choke on a key a newer version added. The cost is that a typo like <c>slackWebhoookUrl</c>
/// is simply ignored, and you discover it when no notification ever arrives. This validator
/// exists to surface exactly that class of problem, on demand (<c>cifail config</c>), without
/// making the loader strict.
/// </para>
///
/// <para>
/// The known-key schema is <b>derived by reflection over <see cref="CiFailConfig"/></b> rather
/// than written out, so it cannot drift when a setting is added.
/// </para>
/// </summary>
public static class ConfigValidator
{
    /// <summary>Max edit distance for a "did you mean" suggestion on an unknown key.</summary>
    private const int SuggestionDistance = 3;

    /// <summary>
    /// Validate the config file at <paramref name="path"/> (default <see cref="CiFailPaths.ConfigPath"/>).
    /// A missing file is valid — cifail is designed to run with no config at all — and yields no
    /// diagnostics. A file that can't be parsed yields a single error rather than throwing.
    /// </summary>
    public static IReadOnlyList<ConfigDiagnostic> Validate(string? path = null)
    {
        path ??= CiFailPaths.ConfigPath;
        var diagnostics = new List<ConfigDiagnostic>();

        if (!File.Exists(path)) return diagnostics;

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new ConfigDiagnostic(DiagnosticSeverity.Error, "", $"could not read the config file — {ex.Message}"));
            return diagnostics;
        }

        if (string.IsNullOrWhiteSpace(text)) return diagnostics;

        // No root means either a parse failure (already reported) or an empty document — either
        // way there is nothing further to say, and re-deserializing would only report the same
        // syntax error a second time.
        var root = ParseRoot(text, diagnostics);
        if (root is null) return diagnostics;

        WalkKeys(root, typeof(CiFailConfig), prefix: "", diagnostics);

        // The document is well-formed YAML but may still not map onto the config types (a string
        // where a number belongs, say), so the load stays guarded.
        CiFailConfig config;
        try
        {
            config = ConfigLoader.LoadFile(path);
        }
        catch (ConfigException ex)
        {
            diagnostics.Add(new ConfigDiagnostic(DiagnosticSeverity.Error, "", ex.Message, ex.Line));
            return diagnostics;
        }

        CheckValues(config, diagnostics);
        return diagnostics;
    }

    private static YamlMappingNode? ParseRoot(string text, List<ConfigDiagnostic> diagnostics)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count == 0) return null;

            if (stream.Documents[0].RootNode is YamlMappingNode mapping) return mapping;

            diagnostics.Add(new ConfigDiagnostic(DiagnosticSeverity.Error, "",
                "the top level of config.yaml must be a mapping of settings (e.g. `database:`)."));
            return null;
        }
        catch (YamlException ex)
        {
            diagnostics.Add(new ConfigDiagnostic(DiagnosticSeverity.Error, "",
                $"could not parse the YAML — {ex.Message}", (int)ex.Start.Line));
            return null;
        }
    }

    /// <summary>
    /// Compare every key in the document against the reflected shape of the config type,
    /// recursing into nested config objects.
    /// </summary>
    private static void WalkKeys(YamlMappingNode node, Type type, string prefix, List<ConfigDiagnostic> diagnostics)
    {
        var shape = Shape(type);

        foreach (var (keyNode, valueNode) in node.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { } key }) continue;

            var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
            var line = keyNode.Start.Line > 0 ? (int)keyNode.Start.Line : (int?)null;

            // Ordinal, because that is how YamlDotNet itself matches after applying the
            // camelCase convention — so `slackwebhookurl` really is ignored at load time.
            if (!shape.TryGetValue(key, out var property))
            {
                var suggestion = Suggest(key, shape.Keys);
                var hint = suggestion is null ? "" : $" — did you mean '{suggestion}'?";
                diagnostics.Add(new ConfigDiagnostic(DiagnosticSeverity.Warning, path,
                    $"unknown setting '{path}', so it is ignored{hint}", line));
                continue;
            }

            if (IsNestedConfig(property.PropertyType) && valueNode is YamlMappingNode child)
                WalkKeys(child, property.PropertyType, path, diagnostics);
        }
    }

    private static void CheckValues(CiFailConfig config, List<ConfigDiagnostic> diagnostics)
    {
        void Error(string path, string message) =>
            diagnostics.Add(new ConfigDiagnostic(DiagnosticSeverity.Error, path, message));
        void Warn(string path, string message) =>
            diagnostics.Add(new ConfigDiagnostic(DiagnosticSeverity.Warning, path, message));

        // Database. An unknown provider is only a warning: the slim binary genuinely lacks the
        // external engines, so this same file is correct when run under the Docker/full build.
        var provider = config.Database.Provider;
        if (string.IsNullOrWhiteSpace(provider))
            Error("database.provider", "is empty; remove it to use the sqlite default.");
        else if (StoreRegistry.Get(provider) is null)
            Warn("database.provider", $"'{provider}' is not available in this build " +
                $"(here: {string.Join(", ", StoreRegistry.AvailableNames)}). The external engines " +
                "ship in the Docker/full build.");

        // AI.
        if (string.IsNullOrWhiteSpace(config.Ai.Provider))
            Error("ai.provider", "is empty; remove it to use the ollama default.");
        else if (AiRegistry.Get(config.Ai.Provider) is null)
            Warn("ai.provider", $"unknown AI provider '{config.Ai.Provider}' " +
                $"(known: {string.Join(", ", AiRegistry.AvailableNames)}).");

        if (config.Ai.EmbeddingDimensions <= 0)
            Error("ai.embeddingDimensions", "must be greater than 0.");
        if (config.Ai.Limits.MaxCallsPerRun < 0)
            Error("ai.limits.maxCallsPerRun", "cannot be negative (use 0 for unlimited).");
        if (config.Ai.Limits.MaxCallsPerMinute < 0)
            Error("ai.limits.maxCallsPerMinute", "cannot be negative (use 0 for unlimited).");
        if (config.Ai.Limits.MaxRequestChars < 0)
            Error("ai.limits.maxRequestChars", "cannot be negative (use 0 for no cap).");

        // Rules. A directory that isn't there is the whole reason this check exists: a pack that
        // never loads looks identical to a rule that doesn't match.
        foreach (var path in config.Rules.Paths)
        {
            if (string.IsNullOrWhiteSpace(path))
                Warn("rules.paths", "contains an empty entry, which is ignored.");
            else if (!Directory.Exists(path))
                Warn("rules.paths", $"'{path}' does not exist, so no packs are loaded from it.");
        }

        // Notifications.
        var n = config.Notifications;
        if (n.DedupeSeconds < 0)
            Error("notifications.dedupeSeconds", "cannot be negative (use 0 to disable deduplication).");

        foreach (var e in n.Events)
        {
            if (Notification.ParseEvent(e) is not null) continue;
            var suggestion = Suggest(e, KnownEventKeys);
            var hint = suggestion is null ? "" : $" — did you mean '{suggestion}'?";
            Warn("notifications.events", $"unknown event '{e}', so it is ignored{hint} " +
                $"(known: {string.Join(", ", KnownEventKeys)})");
        }

        CheckUrl("notifications.slackWebhookUrl", n.SlackWebhookUrl);
        CheckUrl("notifications.webhookUrl", n.WebhookUrl);
        CheckUrl("notifications.discordWebhookUrl", n.DiscordWebhookUrl);
        CheckUrl("notifications.teamsWebhookUrl", n.TeamsWebhookUrl);

        if (n.Smtp is { } smtp)
        {
            if (string.IsNullOrWhiteSpace(smtp.Host)) Error("notifications.smtp.host", "is required.");
            if (string.IsNullOrWhiteSpace(smtp.From)) Error("notifications.smtp.from", "is required.");
            if (string.IsNullOrWhiteSpace(smtp.To)) Error("notifications.smtp.to", "is required.");
            if (smtp.Port is <= 0 or > 65535) Error("notifications.smtp.port", "must be between 1 and 65535.");
            if (string.IsNullOrWhiteSpace(smtp.PasswordEnv))
                Warn("notifications.smtp.passwordEnv", "is empty, so no password will be sent.");
        }

        if (n.GitHub is { } gh)
        {
            if (string.IsNullOrWhiteSpace(gh.Repo))
                Error("notifications.gitHub.repo", "is required (owner/name).");
            else if (gh.Repo.Count(c => c == '/') != 1)
                Error("notifications.gitHub.repo", $"'{gh.Repo}' should look like owner/name.");
            if (string.IsNullOrWhiteSpace(gh.TokenEnv))
                Error("notifications.gitHub.tokenEnv", "is required (the env var holding the API token).");
        }

        void CheckUrl(string path, string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                Error(path, "must be an absolute http(s) URL.");
        }
    }

    private static readonly string[] KnownEventKeys = { "new-failure", "recurrence", "resolved" };

    /// <summary>
    /// The settable public properties of a config type, keyed by the YAML name the loader's
    /// camelCase convention produces.
    /// </summary>
    private static Dictionary<string, PropertyInfo> Shape(Type type)
    {
        var shape = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanWrite || p.GetIndexParameters().Length > 0) continue;
            shape[CamelCase(p.Name)] = p;
        }
        return shape;
    }

    /// <summary>A nested settings object we should recurse into (as opposed to a scalar or list).</summary>
    private static bool IsNestedConfig(Type type) =>
        type.IsClass && type != typeof(string) && type.Namespace == typeof(CiFailConfig).Namespace;

    private static string CamelCase(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>The closest known key, if one is close enough to be worth suggesting.</summary>
    private static string? Suggest(string unknown, IEnumerable<string> known)
    {
        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var candidate in known)
        {
            // A pure case difference is the most common typo and always worth surfacing.
            if (string.Equals(candidate, unknown, StringComparison.OrdinalIgnoreCase)) return candidate;

            var distance = Distance(unknown, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        // Scale the tolerance to the word: "ai" must not suggest every 2-letter neighbour.
        var limit = Math.Min(SuggestionDistance, Math.Max(1, unknown.Length / 3));
        return bestDistance <= limit ? best : null;
    }

    /// <summary>Levenshtein distance, case-insensitive, two-row variant.</summary>
    private static int Distance(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
