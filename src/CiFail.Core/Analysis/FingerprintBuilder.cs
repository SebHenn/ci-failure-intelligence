using System.Security.Cryptography;
using System.Text;
using CiFail.Core.Ingest;
using CiFail.Core.Models;

namespace CiFail.Core.Analysis;

/// <summary>
/// Builds a stable <see cref="FailureFingerprint"/> for a log + (optional) winning
/// match. The signature is derived from scrubbed text so cosmetic differences
/// (paths, numbers, GUIDs) don't change the fingerprint.
/// </summary>
public static class FingerprintBuilder
{
    public static FailureFingerprint Build(LogDocument log, RuleMatch? rootCause)
    {
        string ruleId = rootCause?.Rule.Id ?? "unknown";

        // Prefer the matched line as the signature; otherwise use the most
        // error-like lines so unmatched failures still group sensibly.
        string signatureSource = rootCause is not null
            ? rootCause.MatchedLine
            : string.Join('\n', ErrorLikeLines(log.NormalizedLines, max: 5));

        var scrubbed = LogNormalizer.Scrub(signatureSource);
        var hash = ShortHash($"{ruleId}\n{scrubbed}");

        return new FailureFingerprint { RuleId = ruleId, Hash = hash };
    }

    private static IEnumerable<string> ErrorLikeLines(IReadOnlyList<string> lines, int max)
    {
        var picked = lines
            .Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)
                     || l.Contains("fail", StringComparison.OrdinalIgnoreCase)
                     || l.Contains("exception", StringComparison.OrdinalIgnoreCase))
            .Take(max)
            .ToList();

        // Fall back to the last few non-empty lines (failures usually end the log).
        if (picked.Count == 0)
        {
            picked = lines
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Reverse()
                .Take(max)
                .Reverse()
                .ToList();
        }

        return picked;
    }

    private static string ShortHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        // 12 hex chars is plenty to distinguish failures while staying readable.
        return Convert.ToHexString(bytes, 0, 6).ToLowerInvariant();
    }
}
