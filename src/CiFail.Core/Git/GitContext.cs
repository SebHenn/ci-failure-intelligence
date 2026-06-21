using System.Diagnostics;

namespace CiFail.Core.Git;

/// <summary>
/// An <see cref="IGitRepo"/> backed by the system <c>git</c> executable. Lightweight (no
/// native libgit2 dependency) and degrades gracefully: <see cref="Detect"/> returns null
/// when git isn't installed, the directory isn't a repo, or there are no commits yet.
/// </summary>
public sealed class GitContext : IGitRepo
{
    private readonly string _workingDir;

    private GitContext(string workingDir, string repoId, string head, string? branch, bool isDirty)
    {
        _workingDir = workingDir;
        RepoId = repoId;
        Head = head;
        Branch = branch;
        IsDirty = isDirty;
    }

    public string RepoId { get; }
    public string Head { get; }
    public string? Branch { get; }
    public bool IsDirty { get; }

    /// <summary>
    /// Inspect <paramref name="workingDir"/> and return a context, or null when it isn't a
    /// usable git repository (not a repo, git missing, or no commits to correlate against).
    /// </summary>
    public static GitContext? Detect(string workingDir)
    {
        if (!TryRun(workingDir, out var inside, "rev-parse", "--is-inside-work-tree")
            || !string.Equals(inside.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!TryRun(workingDir, out var head, "rev-parse", "HEAD"))
            return null; // no commits yet — nothing to correlate
        head = head.Trim();
        if (string.IsNullOrEmpty(head)) return null;

        // Root commit = stable repo identity across clones. First line handles multi-root repos.
        if (!TryRun(workingDir, out var roots, "rev-list", "--max-parents=0", "HEAD"))
            return null;
        var repoId = roots.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? head;

        string? branch = null;
        if (TryRun(workingDir, out var b, "rev-parse", "--abbrev-ref", "HEAD"))
        {
            b = b.Trim();
            if (b.Length > 0 && !string.Equals(b, "HEAD", StringComparison.Ordinal)) branch = b;
        }

        var isDirty = TryRun(workingDir, out var status, "status", "--porcelain")
                      && status.Trim().Length > 0;

        return new GitContext(workingDir, repoId, head, branch, isDirty);
    }

    public bool IsAncestor(string ancestor, string descendant) =>
        // `merge-base --is-ancestor` signals via exit code: 0 = yes, 1 = no.
        RunExitCode(_workingDir, "merge-base", "--is-ancestor", ancestor, descendant) == 0;

    public IReadOnlyList<string> CommitSubjects(string fromExclusive, string toInclusive)
    {
        if (!TryRun(_workingDir, out var output, "log", "--format=%s", $"{fromExclusive}..{toInclusive}"))
            return Array.Empty<string>();

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    // --- git process plumbing -------------------------------------------------

    private static bool TryRun(string workingDir, out string stdout, params string[] args)
    {
        var (exit, output) = RunCapture(workingDir, args);
        stdout = output;
        return exit == 0;
    }

    private static int RunExitCode(string workingDir, params string[] args) =>
        RunCapture(workingDir, args).ExitCode;

    private static (int ExitCode, string StdOut) RunCapture(string workingDir, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var process = Process.Start(psi);
            if (process is null) return (-1, string.Empty);

            var stdout = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd(); // drain so the process can't block on a full pipe
            process.WaitForExit();
            return (process.ExitCode, stdout);
        }
        catch
        {
            // git not installed / not on PATH, or spawn failure — treat as "no git".
            return (-1, string.Empty);
        }
    }
}
