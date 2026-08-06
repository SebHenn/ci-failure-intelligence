using CiFail.Core.Ingest;
using CiFail.Core.Models;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Ingest;

public class EcosystemDetectorTests
{
    private static LogDocument Doc(string text) => LogNormalizer.Build("test", text);

    [Theory]
    [InlineData("error NU1101: Unable to find package Foo", Ecosystem.Dotnet)]
    [InlineData("npm ERR! code ERESOLVE", Ecosystem.Node)]
    [InlineData("Traceback (most recent call last):\n  File x", Ecosystem.Python)]
    [InlineData("[INFO] BUILD FAILURE\n[ERROR] Foo.java:[1,1] bad", Ecosystem.Java)]
    [InlineData("go build ./...\nmain.go:1:1: undefined: foo", Ecosystem.Go)]
    [InlineData("error[E0432]: unresolved import\n --> src/main.rs:1:1", Ecosystem.Rust)]
    [InlineData("rake aborted!\nLoadError -- nokogiri\napp.rb:1:in `require'", Ecosystem.Ruby)]
    [InlineData("composer install\nCould not find package monolog/monolog\nPHP Fatal error: boom", Ecosystem.Php)]
    [InlineData("g++ -o app main.cpp\nmain.cpp:(.text+0x2a): undefined reference to `foo'\ncollect2: error: ld", Ecosystem.Cpp)]
    [InlineData("docker build -t app .\nDockerfile:4\nERROR: failed to solve", Ecosystem.Infra)]
    [InlineData("terraform apply\n│ Error: Error acquiring the state lock", Ecosystem.Infra)]
    [InlineData("xcodebuild\nMain.swift:3:8: error: no such module 'Foo'\n** BUILD FAILED **", Ecosystem.Swift)]
    [InlineData("> Task :app:processDebugResources\nAAPT: error: resource x not found\ncom.android.foo", Ecosystem.Android)]
    [InlineData("some unrelated process exited with code 7", Ecosystem.Generic)]
    public void Detect_picks_expected_ecosystem(string text, Ecosystem expected)
    {
        EcosystemDetector.Detect(Doc(text)).Should().Be(expected);
    }

    // ---- scoring: markers, not occurrences -------------------------------------------------

    [Fact]
    public void A_repeated_generic_marker_does_not_outweigh_the_real_ecosystem()
    {
        // The bug this replaces: scoring summed total match counts, so a chatty logger emitting
        // thirty "[ERROR] " lines beat the handful of markers that actually identified the log.
        var noise = string.Join("\n", Enumerable.Repeat("[ERROR] something went wrong", 30));
        var log = noise + "\nTraceback (most recent call last):\n  File \"app.py\", line 3\nModuleNotFoundError: No module named 'requests'";

        EcosystemDetector.Detect(Doc(log)).Should().Be(Ecosystem.Python);
    }

    [Fact]
    public void One_marker_repeated_scores_the_same_as_appearing_once()
    {
        var once = EcosystemDetector.Rank(Doc("npm ERR! boom"));
        var many = EcosystemDetector.Rank(Doc(string.Join("\n", Enumerable.Repeat("npm ERR! boom", 20))));

        many.First(r => r.Ecosystem == Ecosystem.Node).Score
            .Should().Be(once.First(r => r.Ecosystem == Ecosystem.Node).Score);
    }

    [Fact]
    public void A_single_weak_marker_is_not_enough_to_claim_an_ecosystem()
    {
        // A stray `gcc` in an otherwise unidentifiable log should not narrow the rule set to C++.
        EcosystemDetector.Detect(Doc("running gcc wrapper\nthe job failed")).Should().Be(Ecosystem.Generic);
    }

    [Fact]
    public void One_strong_marker_is_enough()
    {
        EcosystemDetector.Detect(Doc("Cargo.toml is malformed")).Should().Be(Ecosystem.Rust);
    }

    // ---- collision-prone markers -----------------------------------------------------------

    [Fact]
    public void Permission_denied_does_not_read_as_infrastructure()
    {
        // `denied: ` used to be an infra marker and fired on every "permission denied:" anywhere.
        EcosystemDetector.Detect(Doc("bash: ./run.sh: permission denied: cannot execute"))
            .Should().NotBe(Ecosystem.Infra);
    }

    [Fact]
    public void The_word_gem_in_prose_does_not_read_as_ruby()
    {
        EcosystemDetector.Detect(Doc("this build script is a hidden gem but it exited 1"))
            .Should().NotBe(Ecosystem.Ruby);
    }

    [Fact]
    public void Merely_naming_a_php_file_does_not_read_as_php()
    {
        EcosystemDetector.Detect(Doc("copying index.php to the artifact directory"))
            .Should().NotBe(Ecosystem.Php);
    }

    // ---- mixed and ambiguous logs ----------------------------------------------------------

    [Fact]
    public void Android_beats_java_on_an_android_build()
    {
        // Every Android build is also a Gradle/Java build, so shared markers must not decide it.
        var log = """
            > Task :app:processDebugResources
            [ERROR] something
            ./gradlew assembleDebug
            com.android.builder.internal.aapt.v2.Aapt2Exception: Android resource linking failed
            """;

        EcosystemDetector.Detect(Doc(log)).Should().Be(Ecosystem.Android);
    }

    [Fact]
    public void A_plain_gradle_java_build_is_still_java()
    {
        var log = """
            ./gradlew build
            [ERROR] /src/main/java/com/example/App.java:12: cannot find symbol
            BUILD FAILURE
            """;

        EcosystemDetector.Detect(Doc(log)).Should().Be(Ecosystem.Java);
    }

    [Fact]
    public void A_node_build_that_also_builds_a_docker_image_is_still_node()
    {
        // Nearly every project builds an image in CI, so infra markers must not hijack the log.
        var log = """
            Step 3/8 : RUN npm ci
            docker build -t app .
            npm ERR! code ERESOLVE
            npm ERR! ERESOLVE unable to resolve dependency tree
            node_modules/react
            """;

        EcosystemDetector.Detect(Doc(log)).Should().Be(Ecosystem.Node);
    }

    [Fact]
    public void A_python_wheel_build_that_shells_out_to_gcc_is_still_python()
    {
        var log = """
            Building wheel for cffi
            gcc -pthread -B /opt/conda/compiler_compat
            c/_cffi_backend.c:15:10: fatal error: ffi.h: No such file or directory
            ERROR: Could not find a version that satisfies the requirement cffi
            pip install cffi
            """;

        EcosystemDetector.Detect(Doc(log)).Should().Be(Ecosystem.Python);
    }

    // ---- tie-breaking ----------------------------------------------------------------------

    [Fact]
    public void Rank_orders_by_score_then_by_specificity()
    {
        var ranked = EcosystemDetector.Rank(Doc("npm ERR! code ERESOLVE"));

        ranked.Should().BeInDescendingOrder(r => r.Score);
        ranked[0].Ecosystem.Should().Be(Ecosystem.Node);
    }

    [Fact]
    public void A_tie_is_broken_deterministically_towards_the_more_specific_ecosystem()
    {
        // One strong marker each for Android and Swift; Android is declared more specific, so
        // the outcome is a decision rather than whatever order a dictionary enumerated in.
        var log = "AndroidManifest.xml\nno such module 'Foo'";
        var ranked = EcosystemDetector.Rank(Doc(log));

        var android = ranked.First(r => r.Ecosystem == Ecosystem.Android).Score;
        var swift = ranked.First(r => r.Ecosystem == Ecosystem.Swift).Score;
        android.Should().Be(swift, "this log is a deliberate tie");

        EcosystemDetector.Detect(Doc(log)).Should().Be(Ecosystem.Android);
    }

    [Fact]
    public void Rank_covers_every_detectable_ecosystem_except_generic()
    {
        var ranked = EcosystemDetector.Rank(Doc("anything")).Select(r => r.Ecosystem);

        var expected = Enum.GetValues<Ecosystem>()
            .Where(e => e is not (Ecosystem.Unknown or Ecosystem.Generic));

        ranked.Should().BeEquivalentTo(expected, "a scored ecosystem that is never ranked is unreachable");
    }

    [Fact]
    public void Detect_respects_explicit_override()
    {
        // Log looks like .NET, but caller forces python.
        EcosystemDetector.Detect(Doc("error NU1101: Unable to find package Foo"), "python")
            .Should().Be(Ecosystem.Python);
    }

    [Fact]
    public void Detect_ignores_unknown_override_and_falls_back_to_detection()
    {
        // Detect() itself stays lenient; it is `cifail analyze --type` that now rejects an
        // unknown value up front, so nothing is silently mis-analyzed.
        EcosystemDetector.Detect(Doc("npm ERR! boom"), "not-a-real-type")
            .Should().Be(Ecosystem.Node);
    }

    [Fact]
    public void Every_advertised_type_name_actually_parses()
    {
        // SupportedNamesText is the single source for both the --type help text and its error
        // message. The old hand-written help list had drifted four ecosystems behind reality.
        foreach (var name in EcosystemDetector.SupportedNames)
        {
            EcosystemDetector.TryParse(name, out var parsed).Should().BeTrue($"'{name}' is advertised as valid");
            parsed.Should().NotBe(Ecosystem.Unknown);
        }
    }

    /// <summary>
    /// A tool invocation identifies its ecosystem as surely as an error code does. These are
    /// all real logs that scored below <c>MinimumScore</c> and fell back to <c>generic</c>,
    /// because the markers only knew the ecosystem's *oldest* tool: a yarn or pnpm failure
    /// need never mention its own lockfile, a mypy or ruff run has nothing but a `.py`
    /// suffix, and a SwiftPM build never says "xcodebuild".
    /// </summary>
    [Theory]
    [InlineData("yarn install v1.22.22\nerror Your lockfile needs to be updated", Ecosystem.Node)]
    [InlineData("Run pnpm install\n ERR_PNPM_OUTDATED_LOCKFILE  Cannot install", Ecosystem.Node)]
    [InlineData("> my-app@1.0.0 test\nnpm run test\nfailed", Ecosystem.Node)]
    [InlineData("npx playwright test\n1 failed", Ecosystem.Node)]
    [InlineData("Run mypy src\nsrc/app.py:12: error: bad type  [assignment]", Ecosystem.Python)]
    [InlineData("Run ruff check .\nsrc/app.py:1:1: F401 unused import", Ecosystem.Python)]
    [InlineData("Run poetry install\npyproject.toml changed significantly", Ecosystem.Python)]
    [InlineData("Run swift build -c release\nerror: product 'X' not found.", Ecosystem.Swift)]
    [InlineData("error: The sandbox is not in sync with the Podfile.lock", Ecosystem.Swift)]
    public void A_tool_invocation_identifies_the_ecosystem(string log, Ecosystem expected)
        => EcosystemDetector.Detect(Doc(log)).Should().Be(expected);

    /// <summary>
    /// The counterweight to the theory above: those markers must stay narrow enough that a
    /// passing mention doesn't hijack an unrelated log.
    /// </summary>
    [Theory]
    [InlineData("Installing the npm package would fix this, apparently.")]
    [InlineData("The build produced a black box; a hidden gem of a yarn.")]
    public void A_passing_mention_of_a_tool_does_not_claim_the_log(string log)
        => EcosystemDetector.Detect(Doc(log)).Should().Be(Ecosystem.Generic);

    [Fact]
    public void Every_ecosystem_the_detector_can_return_is_advertised()
    {
        var advertised = EcosystemDetector.SupportedNames
            .Select(n => { EcosystemDetector.TryParse(n, out var e); return e; })
            .ToHashSet();

        var detectable = Enum.GetValues<Ecosystem>().Where(e => e != Ecosystem.Unknown);

        advertised.Should().BeEquivalentTo(detectable,
            "a user cannot force an ecosystem that --type never mentions");
    }
}
