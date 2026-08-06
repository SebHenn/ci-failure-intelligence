using CiFail.Core.Analysis;
using CiFail.Core.Models;
using CiFail.Core.Rules;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Rules;

/// <summary>
/// End-to-end coverage of every shipped rule, over realistic log fixtures.
///
/// <para>
/// Rules are data, so a rule with no fixture is an untested claim: nothing proves the regex ever
/// fires on the output the tool actually produces, and nothing catches it silently breaking. Two
/// thirds of the pack was in that state. <see cref="Every_shipped_rule_has_a_fixture"/> now makes
/// adding a rule without one fail the build.
/// </para>
///
/// <para>
/// Each case asserts the expected rule <b>wins</b> the ranking, not merely that it matched —
/// matching while losing to a vaguer rule is indistinguishable from not working.
/// </para>
/// </summary>
public class RulePackBreadthTests
{
    private static readonly AnalysisService Service = AnalysisService.CreateDefault();

    /// <summary>
    /// fixture → (expected ecosystem, expected winning rule). Rules whose fixture is deliberately
    /// generic (so the generic pack wins) are covered by <see cref="Cross_cutting_generic_rule_wins"/>.
    /// </summary>
    public static TheoryData<string, Ecosystem, string> EcosystemRules => new()
    {
        // dotnet
        { "nuget-nu1101.log", Ecosystem.Dotnet, "nuget-nu1101" },
        { "nuget-nu1605-downgrade.log", Ecosystem.Dotnet, "nuget-nu1605" },
        { "nuget-nu1102.log", Ecosystem.Dotnet, "nuget-nu1102" },
        { "msbuild-msb3073.log", Ecosystem.Dotnet, "msbuild-msb3073" },
        { "csc-compile-error.log", Ecosystem.Dotnet, "csc-compile-error" },
        { "xunit-assert-failed.log", Ecosystem.Dotnet, "xunit-assert-failed" },
        { "dotnet-sdk-not-found.log", Ecosystem.Dotnet, "dotnet-sdk-not-found" },
        { "netsdk-assets-missing.log", Ecosystem.Dotnet, "netsdk-assets-missing" },
        { "nuget-nu1301.log", Ecosystem.Dotnet, "nuget-nu1301" },
        { "msbuild-msb4018.log", Ecosystem.Dotnet, "msbuild-msb4018" },
        { "mstest-assert-failed.log", Ecosystem.Dotnet, "mstest-assert-failed" },
        { "nunit-assert-failed.log", Ecosystem.Dotnet, "nunit-assert-failed" },

        // node
        { "node-eresolve.log", Ecosystem.Node, "npm-eresolve" },
        { "node-missing-module.log", Ecosystem.Node, "npm-missing-module" },
        { "npm-404.log", Ecosystem.Node, "npm-404" },
        { "npm-missing-script.log", Ecosystem.Node, "npm-missing-script" },
        { "node-enoent.log", Ecosystem.Node, "node-enoent" },
        { "jest-test-failed.log", Ecosystem.Node, "jest-test-failed" },
        { "npm-ci-lock-mismatch.log", Ecosystem.Node, "npm-ci-lock-mismatch" },
        { "npm-ebadengine.log", Ecosystem.Node, "npm-ebadengine" },
        { "npm-eintegrity.log", Ecosystem.Node, "npm-eintegrity" },
        { "yarn-frozen-lockfile.log", Ecosystem.Node, "yarn-frozen-lockfile" },
        { "pnpm-frozen-lockfile.log", Ecosystem.Node, "pnpm-frozen-lockfile" },
        { "typescript-compile-error.log", Ecosystem.Node, "typescript-compile-error" },
        { "vitest-test-failed.log", Ecosystem.Node, "vitest-test-failed" },
        { "playwright-test-failed.log", Ecosystem.Node, "playwright-test-failed" },

        // python
        { "python-modulenotfound.log", Ecosystem.Python, "py-module-not-found" },
        { "python-pip-resolution.log", Ecosystem.Python, "pip-resolution-impossible" },
        { "py-import-error.log", Ecosystem.Python, "py-import-error" },
        { "pip-no-matching-version.log", Ecosystem.Python, "pip-no-matching-version" },
        { "py-syntax-error.log", Ecosystem.Python, "py-syntax-error" },
        { "pytest-failed.log", Ecosystem.Python, "pytest-failed" },
        { "pip-wheel-build-failed.log", Ecosystem.Python, "pip-wheel-build-failed" },
        { "pip-externally-managed.log", Ecosystem.Python, "pip-externally-managed" },
        { "poetry-lock-outdated.log", Ecosystem.Python, "poetry-lock-outdated" },
        { "py-attribute-error.log", Ecosystem.Python, "py-attribute-error" },
        { "mypy-type-error.log", Ecosystem.Python, "mypy-type-error" },
        { "ruff-lint-error.log", Ecosystem.Python, "ruff-lint-error" },

        // java
        { "java-compile-error.log", Ecosystem.Java, "java-compile-error" },
        { "maven-dependency-not-found.log", Ecosystem.Java, "maven-dependency-not-found" },
        { "maven-build-failure.log", Ecosystem.Java, "maven-build-failure" },
        { "gradle-build-failed.log", Ecosystem.Java, "gradle-build-failed" },
        { "junit-assertion-failed.log", Ecosystem.Java, "junit-assertion-failed" },
        { "java-out-of-memory.log", Ecosystem.Java, "java-out-of-memory" },
        { "gradle-daemon-disappeared.log", Ecosystem.Java, "gradle-daemon-disappeared" },

        // go
        { "go-undefined.log", Ecosystem.Go, "go-undefined" },
        { "go-mod-mismatch.log", Ecosystem.Go, "go-mod-mismatch" },
        { "go-cannot-find-package.log", Ecosystem.Go, "go-cannot-find-package" },
        { "go-build-failed.log", Ecosystem.Go, "go-build-failed" },
        { "go-test-failed.log", Ecosystem.Go, "go-test-failed" },
        { "go-type-mismatch.log", Ecosystem.Go, "go-type-mismatch" },
        { "go-import-cycle.log", Ecosystem.Go, "go-import-cycle" },
        { "go-version-mismatch.log", Ecosystem.Go, "go-version-mismatch" },

        // rust
        { "rust-error-code.log", Ecosystem.Rust, "rust-error-code" },
        { "rust-unresolved-import.log", Ecosystem.Rust, "rust-unresolved-import" },
        { "cargo-could-not-compile.log", Ecosystem.Rust, "cargo-could-not-compile" },
        { "rust-test-panic.log", Ecosystem.Rust, "rust-test-panic" },
        { "cargo-dependency-resolution.log", Ecosystem.Rust, "cargo-dependency-resolution" },
        { "rust-toolchain-mismatch.log", Ecosystem.Rust, "rust-toolchain-mismatch" },
        { "cargo-lock-out-of-date.log", Ecosystem.Rust, "cargo-lock-out-of-date" },

        // ruby
        { "ruby-bundler-gem.log", Ecosystem.Ruby, "bundler-gem-not-found" },
        { "ruby-loaderror.log", Ecosystem.Ruby, "ruby-load-error" },
        { "rspec-failure.log", Ecosystem.Ruby, "rspec-failure" },
        { "rake-aborted.log", Ecosystem.Ruby, "rake-aborted" },
        { "ruby-syntax-error.log", Ecosystem.Ruby, "ruby-syntax-error" },
        { "ruby-nomethod-error.log", Ecosystem.Ruby, "ruby-nomethod-error" },
        { "bundler-version-conflict.log", Ecosystem.Ruby, "bundler-version-conflict" },

        // php
        { "php-composer-not-found.log", Ecosystem.Php, "composer-package-not-found" },
        { "php-fatal.log", Ecosystem.Php, "php-fatal-uncaught" },
        { "composer-platform-requirement.log", Ecosystem.Php, "composer-platform-requirement" },
        { "php-call-undefined.log", Ecosystem.Php, "php-call-undefined" },
        { "php-parse-error.log", Ecosystem.Php, "php-parse-error" },
        { "phpunit-failures.log", Ecosystem.Php, "phpunit-failures" },

        // cpp
        { "cpp-undefined-reference.log", Ecosystem.Cpp, "cpp-undefined-reference" },
        { "cmake-not-found.log", Ecosystem.Cpp, "cmake-package-not-found" },
        { "cpp-fatal-no-such-file.log", Ecosystem.Cpp, "cpp-fatal-no-such-file" },
        { "cpp-compile-error.log", Ecosystem.Cpp, "cpp-compile-error" },
        { "make-no-rule.log", Ecosystem.Cpp, "make-no-rule" },
        { "cpp-linker-collect2.log", Ecosystem.Cpp, "cpp-linker-collect2" },

        // infra
        { "terraform-state-lock.log", Ecosystem.Infra, "terraform-state-lock" },
        { "docker-copy-failed.log", Ecosystem.Infra, "docker-copy-not-found" },
        { "terraform-provider-unavailable.log", Ecosystem.Infra, "terraform-provider-unavailable" },
        { "terraform-error.log", Ecosystem.Infra, "terraform-error" },
        { "dockerfile-parse-error.log", Ecosystem.Infra, "dockerfile-parse-error" },
        { "terraform-unsupported-argument.log", Ecosystem.Infra, "terraform-unsupported-argument" },

        // swift
        { "swift-no-such-module.log", Ecosystem.Swift, "swift-no-such-module" },
        { "swift-compile-error.log", Ecosystem.Swift, "swift-compile-error" },
        { "xcode-code-signing.log", Ecosystem.Swift, "xcode-code-signing" },
        { "xcode-build-failed.log", Ecosystem.Swift, "xcode-build-failed" },
        { "swift-linker-symbols.log", Ecosystem.Swift, "swift-linker-symbols" },
        { "xctest-failed.log", Ecosystem.Swift, "xctest-failed" },
        { "swift-package-resolution.log", Ecosystem.Swift, "swift-package-resolution" },
        { "swift-missing-product.log", Ecosystem.Swift, "swift-missing-product" },
        { "xcodebuild-no-destination.log", Ecosystem.Swift, "xcodebuild-no-destination" },
        { "cocoapods-sandbox-out-of-sync.log", Ecosystem.Swift, "cocoapods-sandbox-out-of-sync" },

        // android
        { "android-resource.log", Ecosystem.Android, "android-resource-not-found" },
        { "android-duplicate-class.log", Ecosystem.Android, "android-duplicate-class" },
        { "android-manifest-merger.log", Ecosystem.Android, "android-manifest-merger" },
        { "android-sdk-license.log", Ecosystem.Android, "android-sdk-license" },
        { "android-gradle-task-failed.log", Ecosystem.Android, "android-gradle-task-failed" },
        { "android-dexing-error.log", Ecosystem.Android, "android-dexing-error" },
    };

    [Theory]
    [MemberData(nameof(EcosystemRules))]
    public void Detects_ecosystem_and_picks_expected_rule(string fixture, Ecosystem eco, string ruleId)
    {
        var analysis = Service.Analyze(fixture, Fixtures.Load(fixture));

        analysis.Ecosystem.Should().Be(eco);
        analysis.RootCause.Should().NotBeNull("no rule matched {0}", fixture);
        analysis.RootCause!.Rule.Id.Should().Be(ruleId);
    }

    /// <summary>
    /// The cross-cutting rules, on logs with no ecosystem of their own. These assert the rule
    /// wins outright — a generic rule that only ever places second is dead weight.
    /// </summary>
    public static TheoryData<string, string> GenericRules => new()
    {
        { "generic-oom.log", "generic-oom" },
        { "generic-disk-space.log", "generic-disk-space" },
        { "generic-network-dns.log", "generic-network-dns" },
        { "generic-rate-limit.log", "generic-rate-limit" },
        { "generic-docker-build.log", "generic-docker-build" },
        { "generic-command-not-found.log", "generic-command-not-found" },
        { "generic-command-not-found-sh.log", "generic-command-not-found" },
        { "github-actions-error.log", "github-actions-error" },
        { "generic-permission-denied.log", "generic-permission-denied" },
        { "generic-nonzero-exit.log", "generic-nonzero-exit" },
        { "generic-timeout.log", "generic-timeout" },
        { "generic-tls-cert.log", "generic-tls-cert" },
        { "generic-registry-auth.log", "generic-registry-auth" },
        { "generic-segfault.log", "generic-segfault" },
        { "generic-git-auth.log", "generic-git-auth" },
    };

    [Theory]
    [MemberData(nameof(GenericRules))]
    public void Cross_cutting_generic_rule_wins(string fixture, string ruleId)
    {
        var analysis = Service.Analyze(fixture, Fixtures.Load(fixture));

        analysis.RootCause.Should().NotBeNull("no rule matched {0}", fixture);
        analysis.RootCause!.Rule.Id.Should().Be(ruleId);
    }

    [Fact]
    public void Every_shipped_rule_has_a_fixture()
    {
        var covered = EcosystemRules.Select(row => (string)row[2]!)
            .Concat(GenericRules.Select(row => (string)row[1]!))
            .ToHashSet(StringComparer.Ordinal);

        var shipped = RulePackLoader.LoadEmbedded().Select(r => r.Id).ToHashSet(StringComparer.Ordinal);

        shipped.Except(covered).Should().BeEmpty(
            "a rule with no fixture is an untested claim — add one to the table above " +
            "(see CONTRIBUTING.md for the add-a-rule workflow)");
    }

    [Fact]
    public void Every_fixture_in_the_table_names_a_real_rule()
    {
        var shipped = RulePackLoader.LoadEmbedded().Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var referenced = EcosystemRules.Select(row => (string)row[2]!)
            .Concat(GenericRules.Select(row => (string)row[1]!));

        referenced.Should().OnlyContain(id => shipped.Contains(id),
            "a renamed or deleted rule must not leave a stale row behind");
    }

    // ---- capture-group behaviour -----------------------------------------------------------

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

    [Fact]
    public void Captures_the_command_that_was_not_found()
    {
        // The bash and dash wordings differ ("x: command not found" vs "x: not found"), and the
        // rule used to handle neither in its first alternative.
        Service.Analyze("c", Fixtures.Load("generic-command-not-found.log"))
            .RootCause!.Captures["command"].Should().Be("shellcheck");

        Service.Analyze("c", Fixtures.Load("generic-command-not-found-sh.log"))
            .RootCause!.Captures["command"].Should().Be("jq");
    }
}
