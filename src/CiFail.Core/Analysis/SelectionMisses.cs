using System.Text.Json;
using CiFail.Core.Ingest.Reports;

namespace CiFail.Core.Analysis;

/// <summary>
/// What a dotnet-tia run left out: the report written by <c>dotnet tia run --report &lt;file&gt;
/// --report-skipped</c>, reduced to the part this needs.
/// </summary>
/// <param name="Skipped">Fully qualified test name to the test project it belongs to.</param>
public sealed record TiaSelection(
    string? HeadCommit,
    string? BaseRef,
    string? BaseCommit,
    IReadOnlyDictionary<string, string> Skipped);

/// <summary>A failing test the selection had skipped.</summary>
/// <param name="Test">The name as the selection wrote it, which is the one to hand to <c>dotnet tia explain</c>.</param>
public sealed record SelectionMiss(TestFailure Failure, string Test, string Project);

/// <summary>
/// A failure whose report names it too loosely to say which skipped test it is: more than one
/// skipped test ends with its name.
/// </summary>
/// <param name="Candidates">Some of the skipped tests it could be.</param>
public sealed record UnmatchedFailure(TestFailure Failure, IReadOnlyList<string> Candidates);

/// <summary>
/// The outcome of a check. A check with <see cref="Unmatched"/> failures is not a clean one, even
/// with no misses: those failures may be skipped tests, and it is not known which.
/// </summary>
public sealed record SelectionCheck(IReadOnlyList<SelectionMiss> Misses, IReadOnlyList<UnmatchedFailure> Unmatched);

/// <summary>
/// Failures that dotnet-tia's selection had skipped - a test it decided the change could not
/// affect, which then failed once it did run.
///
/// <para>
/// Test impact analysis is only worth having if it does not skip a test that would have failed,
/// and the only place that is ever observed for real is CI: the pull request ran a selection, the
/// default branch ran everything, and one of the tests the selection left out is red. dotnet-tia's
/// own gates prove the absence of misses on repositories someone chose to measure; this is how
/// one is noticed on a repository nobody was measuring.
/// </para>
///
/// <para>
/// A skipped test can also fail for a reason the change had nothing to do with - it was already
/// flaky, or something else broke it later. That is why this names a <b>candidate</b>, and the
/// output points at <c>dotnet tia explain</c>, which says whether anything connects the two.
/// </para>
/// </summary>
public static class SelectionMisses
{
    /// <summary>The dotnet-tia JSON schema this reads. A report of another version is refused rather than misread.</summary>
    public const int SupportedSchemaVersion = 1;

    /// <exception cref="FormatException">Not a dotnet-tia report, a version this does not read, or one written without <c>--report-skipped</c>.</exception>
    public static TiaSelection ReadReport(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"not JSON: {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number)
            {
                throw new FormatException("not a dotnet-tia report: it has no schemaVersion.");
            }

            if (version.GetInt32() != SupportedSchemaVersion)
            {
                throw new FormatException(
                    $"dotnet-tia report schema {version.GetInt32()} is not supported; this reads version {SupportedSchemaVersion}.");
            }

            if (!root.TryGetProperty("skippedTests", out var skippedTests) || skippedTests.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException(
                    "the report does not list skipped tests. Write it with `dotnet tia run --report <file> --report-skipped`.");
            }

            var skipped = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var project in skippedTests.EnumerateArray())
            {
                var name = project.TryGetProperty("project", out var p) ? p.GetString() ?? "" : "";
                if (!project.TryGetProperty("tests", out var tests) || tests.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var test in tests.EnumerateArray())
                {
                    if (test.GetString() is { Length: > 0 } fullName)
                        skipped.TryAdd(fullName, name);
                }
            }

            return new TiaSelection(String(root, "headCommit"), String(root, "baseRef"), String(root, "baseCommit"), skipped);
        }
    }

    /// <summary>The failures whose test the selection skipped. See <see cref="Check"/>.</summary>
    public static IReadOnlyList<SelectionMiss> Find(TiaSelection selection, IEnumerable<TestFailure> failures) =>
        Check(selection, failures).Misses;

    /// <summary>
    /// The failures whose test the selection skipped, and those that could not be told apart. A
    /// name is matched exactly, with a parameterised test's arguments ignored - the selection works
    /// at method granularity. A report that records only a method name is matched on the
    /// class-qualified suffix, and only when exactly one skipped test ends with it: a guess between
    /// two would be a claim this cannot back. Such a failure is returned as unmatched rather than
    /// dropped, because leaving it out would make the check read as clean (#56).
    /// </summary>
    public static SelectionCheck Check(TiaSelection selection, IEnumerable<TestFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(failures);

        var misses = new List<SelectionMiss>();
        var unmatched = new List<UnmatchedFailure>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var failure in failures)
        {
            var (test, candidates) = Match(selection.Skipped, failure);
            if (test is not null)
            {
                if (seen.Add(test))
                    misses.Add(new SelectionMiss(failure, test, selection.Skipped[test]));
            }
            else if (candidates.Count > 1 && seen.Add("?" + failure.FullName))
            {
                unmatched.Add(new UnmatchedFailure(failure, candidates));
            }
        }

        return new SelectionCheck(misses, unmatched);
    }

    /// <summary>How many of an unmatched failure's candidates are kept to show.</summary>
    private const int CandidatesShown = 5;

    private static (string? Test, IReadOnlyList<string> Candidates) Match(IReadOnlyDictionary<string, string> skipped, TestFailure failure)
    {
        // The method a TRX definition names is what ran; the class beside it makes it exact.
        var qualifiedMethod = failure is { Classname: { Length: > 0 } cls, Method: { Length: > 0 } method } ? $"{cls}.{method}" : null;

        foreach (var candidate in new[] { failure.FullName, failure.Name, qualifiedMethod }
                     .OfType<string>().Select(WithoutArguments).Distinct(StringComparer.Ordinal))
        {
            if (skipped.ContainsKey(candidate))
                return (candidate, []);
        }

        // A TRX definition's class is the class that ran, so a failure it names that is not among
        // the skipped tests ran, whatever other class has a method of the same name. A JUnit
        // classname is not that reliable (some converters write the suite or the assembly there),
        // so a JUnit failure still falls back to the suffix.
        if (failure.Method is not null && !string.IsNullOrWhiteSpace(failure.Classname))
            return (null, []);

        var name = WithoutArguments(failure.Method is { Length: > 0 } m ? m : failure.Name);
        if (name.Length == 0)
            return (null, []);

        var suffix = "." + name;
        var endingWith = skipped.Keys.Where(k => k.EndsWith(suffix, StringComparison.Ordinal)).Take(CandidatesShown).ToList();
        return endingWith.Count == 1 ? (endingWith[0], []) : (null, endingWith);
    }

    /// <summary><c>Ns.Cls.Adds(x: 1, y: 2)</c> is the method <c>Ns.Cls.Adds</c>.</summary>
    internal static string WithoutArguments(string name)
    {
        var open = name.IndexOf('(', StringComparison.Ordinal);
        return (open < 0 ? name : name[..open]).Trim();
    }

    private static string? String(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
