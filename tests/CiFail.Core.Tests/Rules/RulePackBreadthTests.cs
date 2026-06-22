using CiFail.Core.Analysis;
using CiFail.Core.Models;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Rules;

/// <summary>End-to-end coverage of the node/python/generic rule packs over fixtures.</summary>
public class RulePackBreadthTests
{
    private static readonly AnalysisService Service = AnalysisService.CreateDefault();

    [Theory]
    [InlineData("node-eresolve.log", Ecosystem.Node, "npm-eresolve")]
    [InlineData("node-missing-module.log", Ecosystem.Node, "npm-missing-module")]
    [InlineData("python-modulenotfound.log", Ecosystem.Python, "py-module-not-found")]
    [InlineData("python-pip-resolution.log", Ecosystem.Python, "pip-resolution-impossible")]
    [InlineData("java-compile-error.log", Ecosystem.Java, "java-compile-error")]
    [InlineData("maven-dependency-not-found.log", Ecosystem.Java, "maven-dependency-not-found")]
    [InlineData("go-undefined.log", Ecosystem.Go, "go-undefined")]
    [InlineData("go-mod-mismatch.log", Ecosystem.Go, "go-mod-mismatch")]
    [InlineData("rust-error-code.log", Ecosystem.Rust, "rust-error-code")]
    [InlineData("ruby-bundler-gem.log", Ecosystem.Ruby, "bundler-gem-not-found")]
    [InlineData("ruby-loaderror.log", Ecosystem.Ruby, "ruby-load-error")]
    public void Detects_ecosystem_and_picks_expected_rule(string fixture, Ecosystem eco, string ruleId)
    {
        var analysis = Service.Analyze(fixture, Fixtures.Load(fixture));

        analysis.Ecosystem.Should().Be(eco);
        analysis.RootCause.Should().NotBeNull();
        analysis.RootCause!.Rule.Id.Should().Be(ruleId);
    }

    [Fact]
    public void Captures_module_name_for_node_missing_module()
    {
        var analysis = Service.Analyze("n", Fixtures.Load("node-missing-module.log"));
        analysis.RootCause!.Captures["module"].Should().Be("lodahs");
        analysis.RootCause.Fix.Should().Contain("lodahs");
    }

    [Fact]
    public void Captures_module_name_for_python_modulenotfound()
    {
        var analysis = Service.Analyze("p", Fixtures.Load("python-modulenotfound.log"));
        analysis.RootCause!.Captures["module"].Should().Be("requests");
    }

    [Fact]
    public void Generic_oom_rule_matches_heap_out_of_memory()
    {
        var analysis = Service.Analyze("g", Fixtures.Load("generic-oom.log"));

        analysis.HasMatch.Should().BeTrue();
        // Generic rules apply across ecosystems; the OOM rule should be the top signal.
        analysis.Matches.Should().Contain(m => m.Rule.Id == "generic-oom");
    }

    [Theory]
    [InlineData("generic-disk-space.log", "generic-disk-space")]
    [InlineData("generic-network-dns.log", "generic-network-dns")]
    [InlineData("generic-rate-limit.log", "generic-rate-limit")]
    [InlineData("generic-docker-build.log", "generic-docker-build")]
    public void Cross_cutting_generic_rule_matches(string fixture, string ruleId)
    {
        var analysis = Service.Analyze(fixture, Fixtures.Load(fixture));

        analysis.HasMatch.Should().BeTrue();
        analysis.Matches.Should().Contain(m => m.Rule.Id == ruleId);
    }

    [Fact]
    public void Captures_error_code_for_rust()
    {
        var analysis = Service.Analyze("r", Fixtures.Load("rust-error-code.log"));
        analysis.RootCause!.Captures["code"].Should().Be("E0425");
        analysis.RootCause.Fix.Should().Contain("E0425");
    }

    [Fact]
    public void Captures_symbol_for_go_undefined()
    {
        var analysis = Service.Analyze("g", Fixtures.Load("go-undefined.log"));
        analysis.RootCause!.Captures["symbol"].Should().Be("render.JSONResponse");
    }
}
