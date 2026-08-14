using System.Diagnostics;
using System.Runtime.InteropServices;
using CiFail.Cli.Output;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CiFail.Cli.Commands;

/// <summary>
/// `cifail init` — install opt-in git hooks (post-commit, post-merge) that run
/// `cifail reconcile`, so past failures get auto-resolved hands-off as you commit/merge.
/// Safe to re-run; refuses to clobber unrelated existing hooks.
/// </summary>
public sealed class InitCommand : Command<InitCommand.Settings>
{
    private const string Marker = "# installed by cifail init";
    private static readonly string[] Hooks = { "post-commit", "post-merge" };

    public sealed class Settings : OutputSettings
    {
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var hooksDir = ResolveHooksDir(Directory.GetCurrentDirectory());
        if (hooksDir is null)
        {
            CliConsole.Error("cifail init only works inside a git repository.");
            return ExitCodes.DependencyUnavailable;
        }

        Directory.CreateDirectory(hooksDir);
        var script = BuildScript();

        foreach (var hook in Hooks)
        {
            var path = Path.Combine(hooksDir, hook);
            if (File.Exists(path) && !File.ReadAllText(path).Contains(Marker))
            {
                CliConsole.Warn($"{hook} already exists and wasn't created by cifail — left untouched.");
                CliConsole.Hint($"  [grey]Add this line yourself to enable it:[/] {ReconcileLine()}");
                continue;
            }

            File.WriteAllText(path, script);
            MakeExecutable(path);
            CliConsole.Out.MarkupLine($"[green]{Glyphs.Check}[/] installed {hook} hook");
        }

        CliConsole.Hint("[grey]cifail will now auto-resolve fixed failures on each commit/merge.[/]");
        return ExitCodes.Ok;
    }

    private static string? ResolveHooksDir(string workingDir)
    {
        // `git rev-parse --git-path hooks` returns the correct hooks dir even for worktrees.
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
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("--git-path");
            psi.ArgumentList.Add("hooks");

            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) return null;

            var rel = output.Trim();
            if (rel.Length == 0) return null;
            return Path.IsPathRooted(rel) ? rel : Path.Combine(workingDir, rel);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildScript() =>
        "#!/bin/sh\n" + Marker + "\n" + ReconcileLine() + "\n";

    private static string ReconcileLine() =>
        // Best effort: never block the commit/merge if cifail isn't on PATH or errors.
        $"{CifailInvocation()} reconcile >/dev/null 2>&1 || true";

    private static string CifailInvocation()
    {
        var exe = Environment.ProcessPath;
        // When run via `dotnet`, ProcessPath is the dotnet host — fall back to PATH lookup.
        if (exe is not null &&
            Path.GetFileNameWithoutExtension(exe).Equals("cifail", StringComparison.OrdinalIgnoreCase))
            return exe.Contains(' ') ? $"\"{exe}\"" : exe;
        return "cifail";
    }

    private static void MakeExecutable(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return; // hooks run via sh; mode is moot
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch { /* non-fatal: hook still runnable via `sh hookpath` */ }
    }
}
