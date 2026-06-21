namespace CiFail.Core.Git;

/// <summary>
/// The git facts cifail needs to correlate failures with the commits that fix them.
/// Abstracted so the reconciler can be unit-tested with a fake; the real implementation
/// (<see cref="GitContext"/>) shells out to the system <c>git</c>.
/// </summary>
public interface IGitRepo
{
    /// <summary>A stable identity for this repository (its root commit), shared by all clones.</summary>
    string RepoId { get; }

    /// <summary>The current HEAD commit SHA.</summary>
    string Head { get; }

    /// <summary>The current branch, or null when detached.</summary>
    string? Branch { get; }

    /// <summary>True when the working tree has uncommitted changes.</summary>
    bool IsDirty { get; }

    /// <summary>True when <paramref name="ancestor"/> is an ancestor of <paramref name="descendant"/>.</summary>
    bool IsAncestor(string ancestor, string descendant);

    /// <summary>Commit subjects in the range <c>(fromExclusive, toInclusive]</c>, newest first.</summary>
    IReadOnlyList<string> CommitSubjects(string fromExclusive, string toInclusive);
}
