using System.Text;
using System.Xml.Linq;

namespace CiFail.Core.Ingest.Reports;

/// <summary>The shape of an input cifail was given.</summary>
public enum ReportFormat
{
    /// <summary>Sniff by extension / root element (the default).</summary>
    Auto,

    /// <summary>A plain build/test log — analyzed as a whole (the original behaviour).</summary>
    Log,

    /// <summary>JUnit XML (<c>&lt;testsuites&gt;</c>/<c>&lt;testsuite&gt;</c>) — the de-facto CI standard.</summary>
    JUnit,

    /// <summary>.NET TRX (<c>&lt;TestRun&gt;</c>) from <c>dotnet test --logger trx</c>.</summary>
    Trx,
}

/// <summary>One failing test extracted from a structured report.</summary>
/// <param name="Name">The test method/case name.</param>
/// <param name="Classname">Owning class/suite, when the format records it (JUnit).</param>
/// <param name="Message">The assertion/error message.</param>
/// <param name="Details">Stack trace or extra detail, when present.</param>
public sealed record TestFailure(string Name, string? Classname, string Message, string? Details)
{
    /// <summary>The fully-qualified test name (class + method when both are known).</summary>
    public string FullName => string.IsNullOrWhiteSpace(Classname) ? Name : $"{Classname}.{Name}";

    /// <summary>
    /// Render this failure as the normalized log text fed into the analysis pipeline, so the
    /// same rules/similarity/fingerprinting work per failing test as they do on a raw log.
    /// </summary>
    public string ToLogText()
    {
        var sb = new StringBuilder();
        sb.Append("FAILED: ").AppendLine(FullName);
        if (!string.IsNullOrWhiteSpace(Message)) sb.AppendLine(Message.Trim());
        if (!string.IsNullOrWhiteSpace(Details)) sb.AppendLine(Details.Trim());
        return sb.ToString();
    }
}

/// <summary>
/// Parses machine-readable test reports (JUnit XML, .NET TRX) into <see cref="TestFailure"/>s.
/// BCL-only (<see cref="System.Xml.Linq"/>), so it ships in the slim binary. Namespaces are
/// ignored (matched by local name) so TRX's xmlns and JUnit variants both parse.
/// </summary>
public static class TestReportParser
{
    /// <summary>Parse a format string (auto/log/junit/trx); null/blank => Auto.</summary>
    public static ReportFormat? TryParseFormat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ReportFormat.Auto;
        return value.Trim().ToLowerInvariant() switch
        {
            "auto" => ReportFormat.Auto,
            "log" or "raw" or "text" => ReportFormat.Log,
            "junit" or "xml" => ReportFormat.JUnit,
            "trx" or "dotnet" => ReportFormat.Trx,
            _ => null,
        };
    }

    /// <summary>
    /// Decide a concrete format for an input: explicit extension wins, otherwise sniff the
    /// XML root element. Anything that isn't a recognized report is treated as a plain log.
    /// </summary>
    public static ReportFormat Detect(string? path, string content)
    {
        var ext = string.IsNullOrEmpty(path) ? "" : Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".trx") return ReportFormat.Trx;

        if (content.TrimStart().StartsWith("<", StringComparison.Ordinal))
        {
            try
            {
                var root = XDocument.Parse(content).Root?.Name.LocalName;
                if (string.Equals(root, "TestRun", StringComparison.OrdinalIgnoreCase)) return ReportFormat.Trx;
                if (root is "testsuites" or "testsuite") return ReportFormat.JUnit;
            }
            catch (System.Xml.XmlException)
            {
                // Not well-formed XML — fall through and treat it as a log.
            }
        }

        return ReportFormat.Log;
    }

    /// <summary>
    /// Parse the failing tests out of a report. Throws <see cref="System.Xml.XmlException"/> on
    /// malformed XML (so a forced --format can report the error). For <see cref="ReportFormat.Log"/>
    /// or <see cref="ReportFormat.Auto"/> this returns empty — the caller handles raw logs itself.
    /// </summary>
    public static IReadOnlyList<TestFailure> ParseFailures(ReportFormat format, string content) => format switch
    {
        ReportFormat.JUnit => ParseJUnit(content),
        ReportFormat.Trx => ParseTrx(content),
        _ => Array.Empty<TestFailure>(),
    };

    private static List<TestFailure> ParseJUnit(string content)
    {
        var failures = new List<TestFailure>();
        foreach (var tc in XDocument.Parse(content).Descendants().Where(e => e.Name.LocalName == "testcase"))
        {
            // A failing case carries a <failure> or <error> child; passing/skipped don't.
            var fault = tc.Elements().FirstOrDefault(e => e.Name.LocalName is "failure" or "error");
            if (fault is null) continue;

            failures.Add(new TestFailure(
                Name: tc.Attribute("name")?.Value ?? "(unknown)",
                Classname: tc.Attribute("classname")?.Value,
                Message: fault.Attribute("message")?.Value ?? fault.Value.Trim(),
                Details: string.IsNullOrWhiteSpace(fault.Value) ? null : fault.Value.Trim()));
        }
        return failures;
    }

    private static List<TestFailure> ParseTrx(string content)
    {
        var failures = new List<TestFailure>();
        foreach (var r in XDocument.Parse(content).Descendants().Where(e => e.Name.LocalName == "UnitTestResult"))
        {
            if (!string.Equals(r.Attribute("outcome")?.Value, "Failed", StringComparison.OrdinalIgnoreCase))
                continue;

            var errorInfo = r.Descendants().FirstOrDefault(e => e.Name.LocalName == "ErrorInfo");
            var message = errorInfo?.Elements().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value;
            var stack = errorInfo?.Elements().FirstOrDefault(e => e.Name.LocalName == "StackTrace")?.Value;

            failures.Add(new TestFailure(
                Name: r.Attribute("testName")?.Value ?? "(unknown)",
                Classname: null,
                Message: message?.Trim() ?? "",
                Details: string.IsNullOrWhiteSpace(stack) ? null : stack.Trim()));
        }
        return failures;
    }
}
