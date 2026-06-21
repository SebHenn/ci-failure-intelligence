using CiFail.Core.Models;

namespace CiFail.Core.Ai;

/// <summary>What an <see cref="IAiAnalyzer"/> is given to reason about a failure.</summary>
public sealed class AiRequest
{
    /// <summary>The relevant slice of the (normalized) log to send to the model.</summary>
    public required string LogExcerpt { get; init; }

    /// <summary>Detected ecosystem, for context.</summary>
    public required Ecosystem Ecosystem { get; init; }

    /// <summary>The best rule match, if any — usually low-confidence (that's why AI was asked).</summary>
    public RuleMatch? TopMatch { get; init; }
}
