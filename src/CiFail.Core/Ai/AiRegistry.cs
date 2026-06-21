using System.Collections.Concurrent;
using CiFail.Core.Ai.Providers;

namespace CiFail.Core.Ai;

/// <summary>
/// Holds the available <see cref="IAiProvider"/>s by name. All ship in Core and use only the
/// BCL HttpClient (no SDKs), so they add ~nothing to binary size and are present even in the
/// slim build. Ollama is the local-first default; the hosted providers are opt-in (they need
/// an API key and make outbound calls).
/// </summary>
public static class AiRegistry
{
    private static readonly ConcurrentDictionary<string, IAiProvider> Providers = new(StringComparer.OrdinalIgnoreCase);

    static AiRegistry()
    {
        Register(new OllamaProvider());
        Register(new AnthropicProvider());
        Register(new OpenAiProvider());
    }

    public static void Register(IAiProvider provider) => Providers[provider.Name] = provider;

    public static IAiProvider? Get(string name) =>
        Providers.TryGetValue(name, out var p) ? p : null;

    public static IReadOnlyCollection<string> AvailableNames =>
        Providers.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();
}

/// <summary>Thrown when the configured AI provider name isn't known.</summary>
public sealed class AiProviderNotAvailableException : Exception
{
    public AiProviderNotAvailableException(string requested, IReadOnlyCollection<string> available)
        : base($"AI provider '{requested}' is not known. Available: {string.Join(", ", available)}.")
    {
        Requested = requested;
        Available = available;
    }

    public string Requested { get; }
    public IReadOnlyCollection<string> Available { get; }
}
