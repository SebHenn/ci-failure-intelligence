namespace CiFail.Core.Ai;

/// <summary>
/// An optional, pluggable text-embedding backend, used to compute a dense vector per analysis
/// for server-side similarity search (R10). Separate from <see cref="IAiAnalyzer"/> because not
/// every AI provider offers embeddings. Best-effort: return null (rather than throw) when the
/// model is unavailable, so similarity quietly falls back to the offline TF-IDF path.
/// </summary>
public interface IAiEmbedder
{
    /// <summary>The fixed dimensionality of the vectors this embedder produces.</summary>
    int Dimensions { get; }

    /// <summary>Embed <paramref name="text"/> into a vector, or null if unavailable.</summary>
    float[]? Embed(string text);
}
