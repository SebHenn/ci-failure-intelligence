namespace CiFail.Providers.Tests;

/// <summary>
/// Gates the real-engine integration tests. They need Docker (via Testcontainers), which
/// isn't present in every dev environment, so they run only when <c>CIFAIL_DB_IT=1</c> is
/// set — CI sets it on the Linux runner; locally they're skipped (not failed).
/// </summary>
internal static class IntegrationGate
{
    public static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("CIFAIL_DB_IT"), "1", StringComparison.Ordinal);

    public const string SkipReason =
        "external-DB integration test — set CIFAIL_DB_IT=1 (with Docker available) to run.";
}
