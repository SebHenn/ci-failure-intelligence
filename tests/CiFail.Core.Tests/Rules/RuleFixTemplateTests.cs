using System.Text.RegularExpressions;
using CiFail.Core.Rules;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Rules;

/// <summary>
/// Guards rule-pack quality: a <c>{placeholder}</c> in a fix that has no matching named
/// capture group would leak the literal braces into user output (Interpolate leaves unknown
/// tokens as-is). This catches typos and the optional-capture leak class.
/// </summary>
public class RuleFixTemplateTests
{
    [Fact]
    public void Every_fix_placeholder_maps_to_a_named_capture_group()
    {
        var offenders = new List<string>();

        foreach (var rule in RulePackLoader.LoadEmbedded())
        {
            var placeholders = Regex.Matches(rule.Fix, @"\{(\w+)\}").Select(m => m.Groups[1].Value);

            Regex regex;
            try { regex = new Regex(rule.Match); }
            catch (ArgumentException) { continue; } // regex validity is covered elsewhere
            var groups = regex.GetGroupNames().ToHashSet(StringComparer.Ordinal);

            foreach (var p in placeholders)
                if (!groups.Contains(p))
                    offenders.Add($"{rule.Id}: fix references {{{p}}} but the regex has no such group");
        }

        offenders.Should().BeEmpty(because: string.Join("; ", offenders));
    }
}
