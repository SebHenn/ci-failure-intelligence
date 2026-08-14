using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Xunit;

namespace CiFail.Cli.Tests;

/// <summary>
/// What <c>analyze</c> and <c>gate</c> accept as input (R39).
///
/// <para>
/// All of this used to be a single <c>File.Exists</c> check. <c>cifail analyze logs/</c> reported
/// "file not found: logs/", and a glob only worked if the shell had already expanded it — so the
/// README's own <c>cifail gate --format trx TestResults/*.trx</c> failed on PowerShell and cmd,
/// on a project whose author develops on Windows. These tests pass the pattern to cifail
/// <b>unexpanded</b>, which is the case that was broken.
/// </para>
/// </summary>
[Collection(CliCollection.Name)]
public class InputExpansionTests
{
    private const string Nu1101 =
        "error NU1101: Unable to find package Newtonsoft.Jsn. No packages exist with this id in source(s): nuget.org";

    private static string Dir(CliHarness cli, string name)
    {
        var dir = Path.Combine(cli.Home, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void A_directory_analyzes_every_log_inside_it()
    {
        using var cli = new CliHarness();
        var dir = Dir(cli, "logs");
        File.WriteAllText(Path.Combine(dir, "one.log"), Nu1101);
        File.WriteAllText(Path.Combine(dir, "two.log"), "npm ERR! code E404\nnpm ERR! 404 Not Found");

        var result = cli.Run("analyze", "--no-history", "--no-git", "--json", dir);

        var doc = System.Text.Json.JsonDocument.Parse(result.Stdout);
        doc.RootElement.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void A_directory_walk_reaches_nested_logs()
    {
        using var cli = new CliHarness();
        var dir = Dir(cli, "logs");
        Directory.CreateDirectory(Path.Combine(dir, "nested"));
        File.WriteAllText(Path.Combine(dir, "nested", "deep.log"), Nu1101);

        var result = cli.Run("analyze", "--no-history", "--no-git", dir);

        result.StdoutFlat.Should().Contain("NuGet package not found");
    }

    /// <summary>A directory with nothing analyzable must say so, not silently succeed.</summary>
    [Fact]
    public void An_empty_directory_is_reported()
    {
        using var cli = new CliHarness();
        var dir = Dir(cli, "empty");

        var result = cli.Run("analyze", "--no-history", "--no-git", dir);

        result.ExitCode.Should().Be(ExitCodes.Usage);
        result.StderrFlat.Should().Contain("no logs found in directory");
    }

    /// <summary>
    /// The case the README documented and PowerShell broke: cifail is handed the literal pattern.
    /// </summary>
    [Fact]
    public void An_unexpanded_glob_is_matched_by_cifail_itself()
    {
        using var cli = new CliHarness();
        var dir = Dir(cli, "globs");
        File.WriteAllText(Path.Combine(dir, "a.log"), Nu1101);
        File.WriteAllText(Path.Combine(dir, "b.log"), Nu1101);
        File.WriteAllText(Path.Combine(dir, "notes.md"), "not a log");

        var result = cli.Run("analyze", "--no-history", "--no-git", "--json",
            Path.Combine(dir, "*.log"));

        System.Text.Json.JsonDocument.Parse(result.Stdout)
            .RootElement.GetArrayLength().Should().Be(2, "the .md must not be picked up");
    }

    [Fact]
    public void A_glob_matching_nothing_is_an_error_not_a_silent_success()
    {
        using var cli = new CliHarness();
        var dir = Dir(cli, "globs");

        var result = cli.Run("analyze", "--no-history", "--no-git", Path.Combine(dir, "*.log"));

        result.ExitCode.Should().Be(ExitCodes.Usage);
        result.StderrFlat.Should().Contain("no files match");
    }

    /// <summary>Downloading a job log from a CI provider generally gets you a .gz.</summary>
    [Fact]
    public void A_gzipped_log_is_decompressed()
    {
        using var cli = new CliHarness();
        var path = Path.Combine(cli.Home, "build.log.gz");

        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionMode.Compress))
            gzip.Write(Encoding.UTF8.GetBytes(Nu1101));

        var result = cli.Run("analyze", "--no-history", "--no-git", path);

        result.StdoutFlat.Should().Contain("NuGet package not found");
    }

    [Fact]
    public void A_missing_file_still_reports_file_not_found()
    {
        using var cli = new CliHarness();

        var result = cli.Run("analyze", "--no-history", "--no-git",
            Path.Combine(cli.Home, "nope.log"));

        result.ExitCode.Should().Be(ExitCodes.Usage);
        result.StderrFlat.Should().Contain("file not found");
    }

    /// <summary>gate shares the input handling, so the two can never disagree about the inputs.</summary>
    [Fact]
    public void Gate_accepts_a_directory_too()
    {
        using var cli = new CliHarness();
        var dir = Dir(cli, "logs");
        File.WriteAllText(Path.Combine(dir, "one.log"), Nu1101);
        var baseline = Path.Combine(cli.Home, "baseline.txt");

        var result = cli.Run("gate", "--baseline", baseline, dir);

        result.ExitCode.Should().Be(ExitCodes.Negative, "the failure is not in the baseline yet");
        result.StdoutFlat.Should().Contain("nuget-nu1101");
    }

    [Fact]
    public void Only_the_file_name_may_contain_wildcards()
    {
        using var cli = new CliHarness();

        var result = cli.Run("analyze", "--no-history", "--no-git",
            Path.Combine(cli.Home, "*", "x.log"));

        result.ExitCode.Should().Be(ExitCodes.Usage);
        result.StderrFlat.Should().Contain("only the file name may contain wildcards");
    }
}
