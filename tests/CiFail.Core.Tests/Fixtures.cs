namespace CiFail.Core.Tests;

/// <summary>Helpers for loading the log fixtures copied next to the test assembly.</summary>
internal static class Fixtures
{
    private static readonly string Dir =
        Path.Combine(AppContext.BaseDirectory, "fixtures");

    public static string Load(string name) => File.ReadAllText(Path.Combine(Dir, name));
}
