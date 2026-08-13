namespace CiFail.Cli.Tests;

/// <summary>
/// Locating the repository's own files from the test binary. Tests run out of
/// <c>bin/Debug/net8.0</c>, so a sample or fixture path has to be found by walking up to the
/// solution rather than assumed relative to the working directory.
/// </summary>
internal static class TestPaths
{
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CiFail.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("could not locate the repository root");
    }

    public static string Sample(string name) => Path.Combine(RepoRoot(), "samples", name);

    public static string Fixture(string name) =>
        Path.Combine(RepoRoot(), "tests", "CiFail.Core.Tests", "fixtures", name);
}
