using System.Diagnostics;
using CiFail.Core.Git;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Git;

/// <summary>
/// Exercises <see cref="GitContext"/> against a throwaway real repository. Requires the
/// <c>git</c> executable (present in CI and normal dev machines).
/// </summary>
public class GitContextTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"cifail-git-{Guid.NewGuid():N}");

    public GitContextTests()
    {
        Directory.CreateDirectory(_dir);
        Git("init", "-q", "-b", "main");
        Git("config", "user.email", "test@cifail.dev");
        Git("config", "user.name", "cifail test");
        Git("config", "commit.gpgsign", "false");
    }

    [Fact]
    public void Detect_returns_null_outside_a_repo()
    {
        var plain = Path.Combine(Path.GetTempPath(), $"cifail-nogit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(plain);
        try
        {
            GitContext.Detect(plain).Should().BeNull();
        }
        finally { Directory.Delete(plain, recursive: true); }
    }

    [Fact]
    public void Detect_reports_head_branch_and_repo_identity()
    {
        var a = Commit("first.txt", "one", "first commit");

        var ctx = GitContext.Detect(_dir);
        ctx.Should().NotBeNull();
        ctx!.Head.Should().Be(a);
        ctx.Branch.Should().Be("main");
        ctx.RepoId.Should().Be(a); // root commit
        ctx.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void IsDirty_is_true_with_uncommitted_changes()
    {
        Commit("first.txt", "one", "first commit");
        File.WriteAllText(Path.Combine(_dir, "first.txt"), "changed");

        GitContext.Detect(_dir)!.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void Ancestry_and_commit_subjects_reflect_history()
    {
        var a = Commit("a.txt", "a", "add a");
        var b = Commit("b.txt", "b", "fix the build");

        var ctx = GitContext.Detect(_dir)!;
        ctx.Head.Should().Be(b);
        ctx.IsAncestor(a, b).Should().BeTrue();
        ctx.IsAncestor(b, a).Should().BeFalse();

        var subjects = ctx.CommitSubjects(a, b);
        subjects.Should().ContainSingle().Which.Should().Be("fix the build");
    }

    private string Commit(string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(_dir, file), content);
        Git("add", file);
        Git("commit", "-q", "-m", message);
        var (_, sha) = Run("rev-parse", "HEAD");
        return sha.Trim();
    }

    private void Git(params string[] args)
    {
        var (exit, _) = Run(args);
        exit.Should().Be(0, $"git {string.Join(' ', args)} should succeed");
    }

    private (int ExitCode, string StdOut) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
