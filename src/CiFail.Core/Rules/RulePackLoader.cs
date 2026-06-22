using System.Reflection;
using CiFail.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CiFail.Core.Rules;

/// <summary>
/// Loads rule definitions from embedded default packs and, optionally, user packs
/// dropped in <c>~/.cifail/rules/*.yaml</c>. User rules with a duplicate id override
/// the embedded default of the same id.
/// </summary>
public static class RulePackLoader
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer YamlOut = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    /// <summary>
    /// Serialize rules back to a YAML pack (a sequence of mappings matching the rule-pack schema),
    /// used by <c>suggest-rule --write</c> (R23) to append a drafted rule to a user pack.
    /// </summary>
    public static string Serialize(IEnumerable<RuleDefinition> rules) =>
        YamlOut.Serialize(rules.ToList());

    /// <summary>Default directory for user-supplied rule packs.</summary>
    public static string DefaultUserRulesDir => CiFailPaths.UserRulesDir;

    /// <summary>
    /// Load all rules: embedded defaults first, then any user packs (which override
    /// embedded rules sharing the same id).
    /// </summary>
    public static IReadOnlyList<RuleDefinition> LoadAll(string? userRulesDir = null)
    {
        var byId = new Dictionary<string, RuleDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in LoadEmbedded())
            byId[rule.Id] = rule;

        userRulesDir ??= DefaultUserRulesDir;
        if (Directory.Exists(userRulesDir))
        {
            foreach (var file in Directory.EnumerateFiles(userRulesDir, "*.yaml", SearchOption.AllDirectories))
            {
                foreach (var rule in ParseDocument(File.ReadAllText(file)))
                    byId[rule.Id] = rule;
            }
        }

        return byId.Values.ToList();
    }

    /// <summary>Load only the rule packs embedded in the assembly.</summary>
    public static IReadOnlyList<RuleDefinition> LoadEmbedded()
    {
        var assembly = typeof(RulePackLoader).Assembly;
        var rules = new List<RuleDefinition>();

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                continue;

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            rules.AddRange(ParseDocument(reader.ReadToEnd()));
        }

        return rules;
    }

    /// <summary>
    /// The raw text of every embedded rule-pack resource, keyed by a short pack name
    /// (e.g. "generic.yaml"). Used by validation/explain tooling that needs the source.
    /// </summary>
    public static IReadOnlyList<(string Source, string Yaml)> EmbeddedDocuments()
    {
        var assembly = typeof(RulePackLoader).Assembly;
        var docs = new List<(string, string)>();

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                continue;

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            docs.Add((ShortName(name), reader.ReadToEnd()));
        }

        return docs;
    }

    /// <summary>Reduce a manifest resource name to its trailing file name (e.g. "generic.yaml").</summary>
    private static string ShortName(string resourceName)
    {
        // Embedded names look like "CiFail.Core.rulepacks.generic.yaml"; keep "<file>.yaml".
        var dot = resourceName.LastIndexOf('.', resourceName.Length - ".yaml".Length - 1);
        return dot >= 0 ? resourceName[(dot + 1)..] : resourceName;
    }

    /// <summary>Parse a single YAML document containing a list of rule definitions.</summary>
    public static IReadOnlyList<RuleDefinition> ParseDocument(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return Array.Empty<RuleDefinition>();

        return DeserializeRaw(yaml)
            .Where(r => !string.IsNullOrWhiteSpace(r.Id) && !string.IsNullOrWhiteSpace(r.Match))
            .ToList();
    }

    /// <summary>
    /// Deserialize a rule-pack document <em>without</em> dropping invalid rules. Validation
    /// tooling uses this so it can report missing ids/matches; the normal load path filters.
    /// Throws <see cref="YamlDotNet.Core.YamlException"/> on malformed YAML.
    /// </summary>
    public static IReadOnlyList<RuleDefinition> DeserializeRaw(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return Array.Empty<RuleDefinition>();

        return Yaml.Deserialize<List<RuleDefinition>>(yaml) ?? new List<RuleDefinition>();
    }
}
