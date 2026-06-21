using CiFail.Core.Ai;
using CiFail.Core.Models;

namespace CiFail.Core.Tests.Ai;

/// <summary>
/// A test double for <see cref="IAiAnalyzer"/>: records whether it was consulted and the
/// request it saw, and can be told to either return a canned suggestion or throw (to prove
/// the pipeline swallows AI failures).
/// </summary>
internal sealed class FakeAiAnalyzer : IAiAnalyzer
{
    private readonly bool _throw;

    public FakeAiAnalyzer(bool @throw = false) => _throw = @throw;

    public int Calls { get; private set; }
    public AiRequest? LastRequest { get; private set; }

    public AiSuggestion? Suggest(AiRequest request)
    {
        Calls++;
        LastRequest = request;
        if (_throw)
            throw new InvalidOperationException("model offline");
        return new AiSuggestion
        {
            Model = "fake/test",
            RootCause = "fake root cause",
            Fix = "fake fix",
        };
    }
}
