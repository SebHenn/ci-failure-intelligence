using CiFail.Core.Analysis;
using CiFail.Core.Ingest.Reports;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Ingest;

public class TestReportParserTests
{
    [Theory]
    [InlineData("junit", ReportFormat.JUnit)]
    [InlineData("xml", ReportFormat.JUnit)]
    [InlineData("trx", ReportFormat.Trx)]
    [InlineData("log", ReportFormat.Log)]
    [InlineData("auto", ReportFormat.Auto)]
    [InlineData(null, ReportFormat.Auto)]
    public void TryParseFormat_maps_known_aliases(string? input, ReportFormat expected)
        => TestReportParser.TryParseFormat(input).Should().Be(expected);

    [Fact]
    public void TryParseFormat_returns_null_for_unknown()
        => TestReportParser.TryParseFormat("bogus").Should().BeNull();

    [Fact]
    public void Detect_uses_trx_extension()
        => TestReportParser.Detect("results.trx", "<TestRun></TestRun>").Should().Be(ReportFormat.Trx);

    [Fact]
    public void Detect_sniffs_junit_root_element()
        => TestReportParser.Detect("out.xml", Fixtures.Load("junit-report.xml")).Should().Be(ReportFormat.JUnit);

    [Fact]
    public void Detect_sniffs_trx_root_element()
        => TestReportParser.Detect(null, Fixtures.Load("dotnet-results.trx")).Should().Be(ReportFormat.Trx);

    [Fact]
    public void Detect_falls_back_to_log_for_plain_text()
        => TestReportParser.Detect("build.log", "error NU1101: Unable to find package").Should().Be(ReportFormat.Log);

    [Fact]
    public void Detect_treats_non_wellformed_xml_as_log()
        => TestReportParser.Detect("weird.xml", "<not closed").Should().Be(ReportFormat.Log);

    [Fact]
    public void ParseJUnit_returns_only_failing_cases_with_details()
    {
        var failures = TestReportParser.ParseFailures(ReportFormat.JUnit, Fixtures.Load("junit-report.xml"));

        failures.Should().HaveCount(2); // the passing case is skipped
        var assertion = failures.Single(f => f.Name == "applies_discount");
        assertion.Classname.Should().Be("com.example.OrderTests");
        assertion.FullName.Should().Be("com.example.OrderTests.applies_discount");
        assertion.Message.Should().Contain("expected");
        assertion.ToLogText().Should().Contain("FAILED: com.example.OrderTests.applies_discount");

        failures.Should().Contain(f => f.Name == "loads_config"); // <error> counts too
    }

    [Fact]
    public void ParseTrx_returns_only_failed_results()
    {
        var failures = TestReportParser.ParseFailures(ReportFormat.Trx, Fixtures.Load("dotnet-results.trx"));

        failures.Should().ContainSingle(); // the Passed result is skipped
        var f = failures[0];
        f.Name.Should().Be("CiFail.Core.Tests.OrderTests.AppliesDiscount");
        f.Message.Should().Contain("Assert.Equal");
        f.Details.Should().Contain("OrderTests.cs:line 42");
    }

    [Fact]
    public void ParseFailures_throws_on_malformed_xml()
    {
        var act = () => TestReportParser.ParseFailures(ReportFormat.JUnit, "<testsuites><testcase>");
        act.Should().Throw<System.Xml.XmlException>();
    }

    [Fact]
    public void Each_failing_test_analyzes_to_its_own_fingerprint()
    {
        // The R17 integration: a report's failures each flow through the normal pipeline,
        // producing distinct per-test fingerprints.
        var service = AnalysisService.CreateDefault();
        var failures = TestReportParser.ParseFailures(ReportFormat.JUnit, Fixtures.Load("junit-report.xml"));

        var analyses = failures
            .Select(f => service.Analyze($"report::{f.FullName}", f.ToLogText()))
            .ToList();

        analyses.Should().HaveCount(2);
        analyses.Select(a => a.Fingerprint.ToString()).Should().OnlyHaveUniqueItems();
        analyses.Should().Contain(a => a.Source.Contains("applies_discount"));
    }
}
