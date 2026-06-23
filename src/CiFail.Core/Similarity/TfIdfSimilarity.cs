namespace CiFail.Core.Similarity;

/// <summary>
/// Offline TF-IDF cosine similarity over bag-of-terms vectors. The IDF is computed
/// from the supplied corpus (plus the query) so that terms common to every failure
/// log are down-weighted and distinctive terms dominate. No embeddings, no network.
/// </summary>
public static class TfIdfSimilarity
{
    public readonly record struct Scored<T>(T Item, double Score);

    /// <summary>
    /// Rank <paramref name="corpus"/> by cosine similarity to <paramref name="query"/>,
    /// returning the top <paramref name="topN"/> with score &gt; 0, highest first.
    /// </summary>
    public static IReadOnlyList<Scored<T>> Rank<T>(
        IReadOnlyDictionary<string, int> query,
        IReadOnlyList<(T item, IReadOnlyDictionary<string, int> terms)> corpus,
        int topN)
    {
        if (corpus.Count == 0 || query.Count == 0 || topN <= 0)
            return Array.Empty<Scored<T>>();

        var idf = ComputeIdf(query, corpus.Select(c => c.terms));
        var queryVec = Weight(query, idf);

        var results = new List<Scored<T>>(corpus.Count);
        foreach (var (item, terms) in corpus)
        {
            var score = Cosine(queryVec, Weight(terms, idf));
            if (score > 0) results.Add(new Scored<T>(item, score));
        }

        return results
            .OrderByDescending(r => r.Score)
            .Take(topN)
            .ToList();
    }

    /// <summary>Cosine similarity of two raw term-frequency bags (used by tests).</summary>
    public static double Cosine(
        IReadOnlyDictionary<string, int> a,
        IReadOnlyDictionary<string, int> b)
    {
        var idf = ComputeIdf(a, new[] { b });
        return Cosine(Weight(a, idf), Weight(b, idf));
    }

    /// <summary>
    /// Smoothed IDF over a whole document set — the corpus-wide weighting used by batch consumers
    /// like clustering (R25), where every document is weighted with one shared IDF rather than a
    /// per-query one. Down-weights terms common to many failures so distinctive terms dominate.
    /// </summary>
    public static Dictionary<string, double> Idf(IReadOnlyList<IReadOnlyDictionary<string, int>> documents)
    {
        int n = documents.Count;
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var doc in documents)
            foreach (var term in doc.Keys)
                df[term] = df.GetValueOrDefault(term) + 1;

        var idf = new Dictionary<string, double>(df.Count, StringComparer.Ordinal);
        foreach (var (term, freq) in df)
            idf[term] = Math.Log((1.0 + n) / (1.0 + freq)) + 1.0; // smoothed idf
        return idf;
    }

    /// <summary>Weight a term-frequency bag into a sparse IDF-weighted vector.</summary>
    public static Dictionary<string, double> WeightVector(
        IReadOnlyDictionary<string, int> terms,
        IReadOnlyDictionary<string, double> idf) => Weight(terms, idf);

    /// <summary>Cosine similarity of two pre-weighted sparse vectors (see <see cref="WeightVector"/>).</summary>
    public static double CosineVectors(
        IReadOnlyDictionary<string, double> a,
        IReadOnlyDictionary<string, double> b) => Cosine(a, b);

    private static Dictionary<string, double> ComputeIdf(
        IReadOnlyDictionary<string, int> query,
        IEnumerable<IReadOnlyDictionary<string, int>> documents)
    {
        // Corpus = the query document plus every corpus document.
        var docs = new List<IReadOnlyDictionary<string, int>> { query };
        docs.AddRange(documents);

        int n = docs.Count;
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var doc in docs)
            foreach (var term in doc.Keys)
                df[term] = df.GetValueOrDefault(term) + 1;

        var idf = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (term, freq) in df)
            idf[term] = Math.Log((1.0 + n) / (1.0 + freq)) + 1.0; // smoothed idf
        return idf;
    }

    private static Dictionary<string, double> Weight(
        IReadOnlyDictionary<string, int> terms,
        IReadOnlyDictionary<string, double> idf)
    {
        var vec = new Dictionary<string, double>(terms.Count, StringComparer.Ordinal);
        foreach (var (term, tf) in terms)
            vec[term] = tf * idf.GetValueOrDefault(term, 0.0);
        return vec;
    }

    private static double Cosine(
        IReadOnlyDictionary<string, double> a,
        IReadOnlyDictionary<string, double> b)
    {
        // Iterate the smaller vector for the dot product.
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        double dot = 0;
        foreach (var (term, weight) in small)
            if (large.TryGetValue(term, out var other))
                dot += weight * other;

        double magA = Magnitude(a), magB = Magnitude(b);
        return magA == 0 || magB == 0 ? 0 : dot / (magA * magB);
    }

    private static double Magnitude(IReadOnlyDictionary<string, double> vec)
    {
        double sum = 0;
        foreach (var w in vec.Values) sum += w * w;
        return Math.Sqrt(sum);
    }
}
