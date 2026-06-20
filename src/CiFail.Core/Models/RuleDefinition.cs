namespace CiFail.Core.Models;

/// <summary>
/// A single failure pattern loaded from a YAML rule pack. Deserialized directly
/// from YAML, so property names mirror the documented rule-pack schema.
/// </summary>
public sealed class RuleDefinition
{
    /// <summary>Stable unique identifier, e.g. "nuget-nu1101".</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Ecosystem this rule belongs to (e.g. "dotnet", "node", "generic").</summary>
    public string Ecosystem { get; set; } = "generic";

    /// <summary>Loose grouping, e.g. "dependency", "compile", "test".</summary>
    public string Category { get; set; } = "general";

    /// <summary>Short human-readable summary of the failure.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Regular expression matched against the normalized log. May contain named
    /// groups (e.g. <c>(?&lt;package&gt;...)</c>) that are interpolated into <see cref="Fix"/>.
    /// </summary>
    public string Match { get; set; } = string.Empty;

    /// <summary>Confidence (0..1) assigned to a match of this rule.</summary>
    public double Confidence { get; set; } = 0.5;

    /// <summary>
    /// Fix guidance template. <c>{name}</c> placeholders are replaced with the
    /// corresponding named capture group from <see cref="Match"/>.
    /// </summary>
    public string Fix { get; set; } = string.Empty;

    /// <summary>Optional documentation link.</summary>
    public string? Docs { get; set; }
}
