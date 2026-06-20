namespace CiFail.Core.Storage;

/// <summary>
/// Persistence for analyses and their resolutions. Abstracted so the analysis
/// pipeline doesn't depend on a concrete database, and tests can use an in-memory
/// fake.
/// </summary>
public interface IAnalysisStore
{
    /// <summary>Persist a new analysis; returns its assigned id.</summary>
    long Save(AnalysisRecord record);

    /// <summary>Most recent analyses, newest first.</summary>
    IReadOnlyList<StoredAnalysis> GetRecent(int limit);

    /// <summary>A single analysis by id, or null if not found.</summary>
    StoredAnalysis? GetById(long id);

    /// <summary>Load up to <paramref name="max"/> entries (with term vectors) for similarity.</summary>
    IReadOnlyList<CorpusEntry> LoadCorpus(int max);

    /// <summary>Record how a failure was resolved; returns false if the id is unknown.</summary>
    bool SetResolution(long id, string note);
}
