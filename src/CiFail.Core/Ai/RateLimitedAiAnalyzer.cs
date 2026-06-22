using CiFail.Core.Configuration;
using CiFail.Core.Models;

namespace CiFail.Core.Ai;

/// <summary>
/// A guardrail decorator (R20) around any <see cref="IAiAnalyzer"/>: caps how often the
/// underlying (possibly paid, hosted) model is called, and optionally trims the prompt size,
/// so a misfiring <c>--ai</c> or a busy server can't run up unbounded cost. Limits are off
/// (unlimited) by default — <see cref="Wrap"/> returns the analyzer untouched when nothing is
/// configured, so existing behaviour is unchanged.
///
/// Staying true to the <see cref="IAiAnalyzer"/> contract, hitting a limit is not an error:
/// the call is simply skipped and <c>null</c> is returned, leaving the rules-only result to stand.
/// </summary>
public sealed class RateLimitedAiAnalyzer : IAiAnalyzer
{
    private readonly IAiAnalyzer _inner;
    private readonly AiLimitsConfig _limits;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _recent = new();
    private int _totalCalls;

    public RateLimitedAiAnalyzer(IAiAnalyzer inner, AiLimitsConfig limits, Func<DateTimeOffset>? clock = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _now = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Wrap <paramref name="inner"/> only when at least one limit is set; otherwise return it as-is.</summary>
    public static IAiAnalyzer Wrap(IAiAnalyzer inner, AiLimitsConfig? limits) =>
        limits is { Any: true } ? new RateLimitedAiAnalyzer(inner, limits) : inner;

    public AiSuggestion? Suggest(AiRequest request)
    {
        if (!TryReserve())
            return null;

        var capped = request;
        if (_limits.MaxRequestChars > 0 && request.LogExcerpt.Length > _limits.MaxRequestChars)
            capped = new AiRequest
            {
                LogExcerpt = request.LogExcerpt[.._limits.MaxRequestChars],
                Ecosystem = request.Ecosystem,
                TopMatch = request.TopMatch,
            };

        return _inner.Suggest(capped);
    }

    /// <summary>Atomically check the call budgets and, if there's room, record this call.</summary>
    private bool TryReserve()
    {
        lock (_gate)
        {
            if (_limits.MaxCallsPerRun > 0 && _totalCalls >= _limits.MaxCallsPerRun)
                return false;

            if (_limits.MaxCallsPerMinute > 0)
            {
                var cutoff = _now() - TimeSpan.FromMinutes(1);
                while (_recent.Count > 0 && _recent.Peek() < cutoff)
                    _recent.Dequeue();
                if (_recent.Count >= _limits.MaxCallsPerMinute)
                    return false;
                _recent.Enqueue(_now());
            }

            _totalCalls++;
            return true;
        }
    }
}
