using System.Text.RegularExpressions;
using CiFail.Core.Models;

namespace CiFail.Core.Ingest;

/// <summary>
/// Heuristically detects which ecosystem a log came from by scanning for
/// characteristic markers. Falls back to <see cref="Ecosystem.Generic"/> when no
/// strong signal is found, and respects an explicit caller override.
/// </summary>
public static partial class EcosystemDetector
{
    public static Ecosystem Detect(LogDocument log, string? overrideType = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideType) && TryParse(overrideType, out var forced))
            return forced;

        var text = log.NormalizedText;

        // Score each ecosystem by how many of its markers appear.
        var scores = new Dictionary<Ecosystem, int>
        {
            [Ecosystem.Dotnet] = Count(DotnetMarkers(), text),
            [Ecosystem.Node] = Count(NodeMarkers(), text),
            [Ecosystem.Python] = Count(PythonMarkers(), text),
            [Ecosystem.Java] = Count(JavaMarkers(), text),
            [Ecosystem.Go] = Count(GoMarkers(), text),
            [Ecosystem.Rust] = Count(RustMarkers(), text),
            [Ecosystem.Ruby] = Count(RubyMarkers(), text),
        };

        var best = scores.OrderByDescending(kv => kv.Value).First();
        return best.Value > 0 ? best.Key : Ecosystem.Generic;
    }

    public static bool TryParse(string value, out Ecosystem ecosystem)
    {
        ecosystem = value.Trim().ToLowerInvariant() switch
        {
            "dotnet" or "net" or ".net" or "csharp" or "nuget" or "msbuild" => Ecosystem.Dotnet,
            "node" or "npm" or "js" or "javascript" or "yarn" or "pnpm" => Ecosystem.Node,
            "python" or "py" or "pip" => Ecosystem.Python,
            "java" or "maven" or "mvn" or "gradle" or "kotlin" => Ecosystem.Java,
            "go" or "golang" => Ecosystem.Go,
            "rust" or "cargo" or "rs" => Ecosystem.Rust,
            "ruby" or "rb" or "bundler" or "gem" or "rails" => Ecosystem.Ruby,
            "generic" or "ci" => Ecosystem.Generic,
            _ => Ecosystem.Unknown,
        };
        return ecosystem != Ecosystem.Unknown;
    }

    private static int Count(Regex regex, string text) => regex.Matches(text).Count;

    [GeneratedRegex(@"error (?:NU|MSB|CS)\d{3,5}|\bdotnet\b|\.csproj|warning (?:NU|MSB|CS)\d{3,5}", RegexOptions.IgnoreCase)]
    private static partial Regex DotnetMarkers();

    [GeneratedRegex(@"npm ERR!|yarn|ERESOLVE|node_modules|Cannot find module|pnpm", RegexOptions.IgnoreCase)]
    private static partial Regex NodeMarkers();

    [GeneratedRegex(@"Traceback \(most recent call last\)|ModuleNotFoundError|pip install|ERROR: Could not|ResolutionImpossible", RegexOptions.IgnoreCase)]
    private static partial Regex PythonMarkers();

    [GeneratedRegex(@"\bBUILD FAILURE\b|\[ERROR\] |\bmaven\b|\bpom\.xml\b|gradlew|\.gradle\b|\bjavac\b|OutOfMemoryError|at org\.junit|Exception in thread", RegexOptions.IgnoreCase)]
    private static partial Regex JavaMarkers();

    [GeneratedRegex(@"\bgo build\b|\bgo: \b|\bgo\.mod\b|\bgo\.sum\b|\bgo test\b|cannot find package|undefined: |\bgolang\b", RegexOptions.IgnoreCase)]
    private static partial Regex GoMarkers();

    [GeneratedRegex(@"error\[E\d{2,4}\]|\bcargo\b|Cargo\.toml|could not compile|rustc|--> src/", RegexOptions.IgnoreCase)]
    private static partial Regex RustMarkers();

    [GeneratedRegex(@"\bbundle(?:r)?\b|Gemfile|\brake\b|\bgem\b|LoadError|\(RSpec|rspec|\.rb:\d+:in ", RegexOptions.IgnoreCase)]
    private static partial Regex RubyMarkers();
}
