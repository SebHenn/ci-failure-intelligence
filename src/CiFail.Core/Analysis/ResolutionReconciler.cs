using CiFail.Core.Git;
using CiFail.Core.Storage;

namespace CiFail.Core.Analysis;

/// <summary>
/// Auto-resolves past failures by correlating them with git history. A failure recorded at
/// commit <c>A</c> is marked resolved once cifail is at a descendant commit <c>B</c> and the
/// same fingerprint is not observed there — crediting the commits landed in <c>(A, B]</c>.
/// Manual resolutions always win (only still-open rows are touched).
/// </summary>
public static class ResolutionReconciler
{
    /// <summary>One auto-resolution that was applied.</summary>
    public sealed record Resolved(long Id, string Fingerprint, string Commit, string Note);

    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    /// <summary>
    /// Reconcile open failures for <paramref name="repo"/>. <paramref name="observedFingerprints"/>
    /// are fingerprints seen at the current HEAD (e.g. from the analysis just run) — failures
    /// matching one of those are still happening and are left open. Pass an empty set (the
    /// default) for a pure "anything from an older commit is likely fixed" pass.
    /// </summary>
    public static IReadOnlyList<Resolved> Reconcile(
        IAnalysisStore store, IGitRepo repo, IReadOnlySet<string>? observedFingerprints = null)
    {
        observedFingerprints ??= None;
        var head = repo.Head;
        var resolved = new List<Resolved>();

        foreach (var failure in store.GetOpenFailures(repo.RepoId))
        {
            var at = failure.GitCommit;
            if (string.IsNullOrEmpty(at)) continue;                     // recorded without a commit
            if (string.Equals(at, head, StringComparison.Ordinal)) continue; // still the same commit
            if (observedFingerprints.Contains(failure.Fingerprint)) continue; // still failing now
            if (!repo.IsAncestor(at, head)) continue;                   // not on this line of history

            var note = BuildNote(repo, at, head);
            if (store.SetAutoResolution(failure.Id, head, note))
                resolved.Add(new Resolved(failure.Id, failure.Fingerprint, head, note));
        }

        return resolved;
    }

    private static string BuildNote(IGitRepo repo, string fromExclusive, string toInclusive)
    {
        var shortHead = Short(toInclusive);
        var subjects = repo.CommitSubjects(fromExclusive, toInclusive);
        if (subjects.Count == 0)
            return $"Likely fixed by {shortHead} — auto-detected.";

        var shown = string.Join("\n", subjects.Take(3).Select(s => "  - " + s));
        var more = subjects.Count > 3 ? $"\n  …and {subjects.Count - 3} more commit(s)." : string.Empty;
        return $"Likely fixed by {shortHead} — auto-detected. Commits since the failure:\n{shown}{more}";
    }

    private static string Short(string sha) => sha.Length >= 7 ? sha[..7] : sha;
}
