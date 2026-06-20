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

    /// <summary>Default directory for user-supplied rule packs.</summary>
    public static string DefaultUserRulesDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cifail", "rules");

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

    /// <summary>Parse a single YAML document containing a list of rule definitions.</summary>
    public static IReadOnlyList<RuleDefinition> ParseDocument(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return Array.Empty<RuleDefinition>();

        var rules = Yaml.Deserialize<List<RuleDefinition>>(yaml) ?? new List<RuleDefinition>();
        return rules
            .Where(r => !string.IsNullOrWhiteSpace(r.Id) && !string.IsNullOrWhiteSpace(r.Match))
            .ToList();
    }
}
