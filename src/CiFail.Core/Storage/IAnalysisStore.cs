namespace CiFail.Core.Storage;

/// <summary>
/// Persistence for analyses and their resolutions. Abstracted so the analysis
/// pipeline doesn't depend on a concrete database, and tests can use an in-memory
/// fake.
/// </summary>
public interface IAnalysisStore : IDisposable
{
    /// <summary>Persist a new analysis; returns its assigned id.</summary>
    long Save(AnalysisRecord record);

    /// <summary>Most recent analyses, newest first.</summary>
    IReadOnlyList<StoredAnalysis> GetRecent(int limit);

    /// <summary>A single analysis by id, or null if not found.</summary>
    StoredAnalysis? GetById(long id);

    /// <summary>Load up to <paramref name="max"/> entries (with term vectors) for similarity.</summary>
    IReadOnlyList<CorpusEntry> LoadCorpus(int max);

    /// <summary>
    /// Record a manual resolution (<c>cifail resolve</c>); marks the row resolved with
    /// source "manual". Returns false if the id is unknown. Manual always overrides auto.
    /// </summary>
    bool SetResolution(long id, string note);

    /// <summary>Open (unresolved) failures for one repository, newest first (R3).</summary>
    IReadOnlyList<StoredAnalysis> GetOpenFailures(string repoId);

    /// <summary>
    /// Record a git-correlated auto-resolution: marks the row resolved with source "auto",
    /// crediting <paramref name="resolvedCommit"/>. Returns false if the id is unknown or
    /// the row is already resolved (manual resolutions are never overwritten).
    /// </summary>
    bool SetAutoResolution(long id, string resolvedCommit, string note);
}
