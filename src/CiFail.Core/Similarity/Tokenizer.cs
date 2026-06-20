using System.Text.RegularExpressions;
using CiFail.Core.Ingest;

namespace CiFail.Core.Similarity;

/// <summary>
/// Turns a log into a bag-of-terms (term -> frequency) over scrubbed text, so that
/// volatile noise (paths, numbers, GUIDs) doesn't dominate similarity. Short and
/// purely-numeric tokens are dropped.
/// </summary>
public static partial class Tokenizer
{
    public static IReadOnlyDictionary<string, int> Tokenize(string text)
    {
        var scrubbed = LogNormalizer.Scrub(text);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (Match token in TokenPattern().Matches(scrubbed))
        {
            var t = token.Value;
            if (t.Length < 2) continue;       // drop single chars / leftover placeholders
            if (IsAllDigits(t)) continue;      // raw numbers carry no signal
            counts[t] = counts.GetValueOrDefault(t) + 1;
        }

        return counts;
    }

    private static bool IsAllDigits(string s)
    {
        foreach (var c in s)
            if (!char.IsDigit(c)) return false;
        return true;
    }

    // Words of letters/digits, allowing internal dots/underscores/hyphens so that
    // identifiers like "nu1101", "system.text.json" stay intact.
    [GeneratedRegex(@"[a-z0-9]+(?:[._-][a-z0-9]+)*")]
    private static partial Regex TokenPattern();
}
