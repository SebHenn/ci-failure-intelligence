using CiFail.Core.Analysis;
using CiFail.Core.Ingest.Reports;

namespace CiFail.Core.Tests.Analysis;

/// <summary>
/// Recognising a failure dotnet-tia's selection had skipped. The direction that matters: a test
/// that ran must never be reported as skipped, and a match is only claimed when it is unambiguous.
/// </summary>
public sealed class SelectionMissesTests
{
    private const string Report = """
        {
          "schemaVersion": 1,
          "mode": "selective",
          "baseRef": "origin/main",
          "baseCommit": "aaaa",
          "headCommit": "bbbbbbbbbbbb",
          "skippedTests": [
            { "project": "App.Tests", "tests": ["App.Tests.GridTests.Wraps", "App.Tests.GridTests.Fills", "App.Tests.Other.Wraps2"] },
            { "project": "Lib.Tests", "tests": ["Lib.Tests.ParserTests.Parses"] }
          ]
        }
        """;

    private static TestFailure Failure(string name, string? classname = null) => new(name, classname, "boom", null);

    [Fact]
    public void A_report_is_read_with_its_commits_and_skipped_tests()
    {
        var selection = SelectionMisses.ReadReport(Report);

        Assert.Equal("bbbbbbbbbbbb", selection.HeadCommit);
        Assert.Equal("origin/main", selection.BaseRef);
        Assert.Equal(4, selection.Skipped.Count);
        Assert.Equal("Lib.Tests", selection.Skipped["Lib.Tests.ParserTests.Parses"]);
    }

    [Fact]
    public void A_failure_of_a_skipped_test_is_a_candidate_miss()
    {
        var misses = SelectionMisses.Find(SelectionMisses.ReadReport(Report), [Failure("App.Tests.GridTests.Wraps")]);

        var miss = Assert.Single(misses);
        Assert.Equal("App.Tests.GridTests.Wraps", miss.Test);
        Assert.Equal("App.Tests", miss.Project);
    }

    [Fact]
    public void A_test_that_ran_is_not_a_miss()
    {
        Assert.Empty(SelectionMisses.Find(SelectionMisses.ReadReport(Report), [Failure("App.Tests.GridTests.Draws")]));
    }

    [Fact]
    public void JUnit_class_and_name_are_joined()
    {
        var misses = SelectionMisses.Find(SelectionMisses.ReadReport(Report), [Failure("Parses", "Lib.Tests.ParserTests")]);

        Assert.Equal("Lib.Tests.ParserTests.Parses", Assert.Single(misses).Test);
    }

    [Fact]
    public void A_parameterised_case_is_its_method()
    {
        var misses = SelectionMisses.Find(SelectionMisses.ReadReport(Report), [Failure("App.Tests.GridTests.Fills(size: 3)")]);

        Assert.Equal("App.Tests.GridTests.Fills", Assert.Single(misses).Test);
    }

    [Fact]
    public void A_bare_method_name_matches_only_when_one_skipped_test_ends_with_it()
    {
        var selection = SelectionMisses.ReadReport(Report);

        // TRX from MSTest records the method alone.
        Assert.Equal("Lib.Tests.ParserTests.Parses", Assert.Single(SelectionMisses.Find(selection, [Failure("Parses")])).Test);

        // ".Wraps" ends one name, and "Wraps2" is a different method, not a second match.
        Assert.Equal("App.Tests.GridTests.Wraps", Assert.Single(SelectionMisses.Find(selection, [Failure("Wraps")])).Test);
    }

    [Fact]
    public void An_ambiguous_bare_name_is_not_claimed()
    {
        var selection = SelectionMisses.ReadReport("""
            { "schemaVersion": 1, "skippedTests": [ { "project": "P", "tests": ["A.Tests.Runs", "B.Tests.Runs"] } ] }
            """);

        Assert.Empty(SelectionMisses.Find(selection, [Failure("Runs")]));
    }

    [Fact]
    public void An_ambiguous_bare_name_is_reported_as_not_checked()
    {
        // Dropping it would make the check read as clean when one of the two may be a miss (#56).
        var selection = SelectionMisses.ReadReport("""
            { "schemaVersion": 1, "skippedTests": [ { "project": "P", "tests": ["A.Tests.Runs", "B.Tests.Runs"] } ] }
            """);

        var check = SelectionMisses.Check(selection, [Failure("Runs")]);

        Assert.Empty(check.Misses);
        var unmatched = Assert.Single(check.Unmatched);
        Assert.Equal(["A.Tests.Runs", "B.Tests.Runs"], unmatched.Candidates.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_bare_name_no_skipped_test_has_is_neither_a_miss_nor_unmatched()
    {
        // The test ran: nothing the selection skipped is called that.
        var check = SelectionMisses.Check(SelectionMisses.ReadReport(Report), [Failure("Draws")]);

        Assert.Empty(check.Misses);
        Assert.Empty(check.Unmatched);
    }

    [Fact]
    public void A_trx_failure_whose_class_is_known_is_matched_on_its_class()
    {
        var selection = SelectionMisses.ReadReport("""
            { "schemaVersion": 1, "skippedTests": [ { "project": "P", "tests": ["A.Tests.Runs", "B.Tests.Runs"] } ] }
            """);

        // Two skipped tests share the method name, and the definition says which class ran.
        var check = SelectionMisses.Check(selection, [new TestFailure("Runs", "B.Tests", "boom", null) { Method = "Runs" }]);
        Assert.Equal("B.Tests.Runs", Assert.Single(check.Misses).Test);
        Assert.Empty(check.Unmatched);

        // A class that is not among them ran, whatever else has a method of that name.
        var ran = SelectionMisses.Check(selection, [new TestFailure("Runs", "C.Tests", "boom", null) { Method = "Runs" }]);
        Assert.Empty(ran.Misses);
        Assert.Empty(ran.Unmatched);
    }

    [Fact]
    public void A_display_name_is_matched_through_the_method_that_ran()
    {
        var failure = new TestFailure("wraps at the edge", "App.Tests.GridTests", "boom", null) { Method = "Wraps" };

        Assert.Equal("App.Tests.GridTests.Wraps", Assert.Single(SelectionMisses.Find(SelectionMisses.ReadReport(Report), [failure])).Test);
    }

    [Fact]
    public void Each_test_is_reported_once()
    {
        // Two frameworks of one project fail the same test.
        var misses = SelectionMisses.Find(SelectionMisses.ReadReport(Report),
            [Failure("App.Tests.GridTests.Wraps"), Failure("App.Tests.GridTests.Wraps")]);

        Assert.Single(misses);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "mode": "selective" }""")]
    [InlineData("""{ "schemaVersion": 2, "skippedTests": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "mode": "selective" }""")]
    public void Anything_but_a_report_with_skipped_tests_is_refused(string json)
    {
        Assert.Throws<FormatException>(() => SelectionMisses.ReadReport(json));
    }

    private const string WithSkippedProjects = """
        {
          "schemaVersion": 1,
          "mode": "selective",
          "skippedTests": [ { "project": "App.Tests", "tests": ["App.Tests.GridTests.Wraps"] } ],
          "skippedProjects": [
            { "project": "App.FSharp.Tests", "assembly": "App.FSharp.Tests", "reason": "nothing it references was impacted" }
          ]
        }
        """;

    private static TestFailure From(string assembly, string name, string? classname = null) =>
        new(name, classname, "boom", null) { Assembly = assembly };

    [Fact]
    public void A_report_from_before_skipped_projects_has_none()
    {
        Assert.Empty(SelectionMisses.ReadReport(Report).SkippedProjects);
        Assert.True(SelectionMisses.Check(SelectionMisses.ReadReport(Report), [Failure("App.Tests.GridTests.Draws")]).Complete);
    }

    [Fact]
    public void A_failure_from_a_project_skipped_whole_is_a_candidate_miss()
    {
        // An F# project: dotnet-tia never named its tests, so only the assembly can say (#57).
        // TRX writes the assembly lowercased in storage, so it is compared without case.
        var check = SelectionMisses.Check(SelectionMisses.ReadReport(WithSkippedProjects),
            [From("app.fsharp.tests", "parses a row", "Tests.Csv")]);

        var miss = Assert.Single(check.Misses);
        Assert.Equal("Tests.Csv.parses a row", miss.Test);
        Assert.Equal("App.FSharp.Tests", miss.Project);
        Assert.True(check.Complete);
    }

    [Fact]
    public void A_failure_from_an_assembly_that_ran_is_not_a_miss()
    {
        var check = SelectionMisses.Check(SelectionMisses.ReadReport(WithSkippedProjects),
            [From("App.Tests", "Draws", "App.Tests.GridTests")]);

        Assert.Empty(check.Misses);
        Assert.True(check.Complete);
    }

    [Fact]
    public void A_project_skipped_whole_is_unchecked_when_a_failure_names_no_assembly()
    {
        // It could be from the F# project. Leaving that out would read as a clean check.
        var check = SelectionMisses.Check(SelectionMisses.ReadReport(WithSkippedProjects), [Failure("Tests.Csv.parses a row")]);

        Assert.Empty(check.Misses);
        var project = Assert.Single(check.UncheckedProjects);
        Assert.Equal("App.FSharp.Tests", project.Project);
        Assert.Contains("1 failing test(s) name no assembly", project.Reason, StringComparison.Ordinal);
        Assert.False(check.Complete);
    }

    [Fact]
    public void A_project_skipped_whole_with_no_assembly_is_unchecked()
    {
        var report = WithSkippedProjects.Replace("\"assembly\": \"App.FSharp.Tests\", ", "", StringComparison.Ordinal);

        var check = SelectionMisses.Check(SelectionMisses.ReadReport(report), [From("App.FSharp.Tests", "parses a row", "Tests.Csv")]);

        Assert.Empty(check.Misses);
        Assert.Equal("the dotnet-tia report names no assembly for it", Assert.Single(check.UncheckedProjects).Reason);
    }

    [Fact]
    public void A_full_run_skips_nothing()
    {
        var selection = SelectionMisses.ReadReport("""{ "schemaVersion": 1, "mode": "full", "skippedTests": [] }""");

        Assert.Empty(SelectionMisses.Find(selection, [Failure("App.Tests.GridTests.Wraps")]));
    }
}
