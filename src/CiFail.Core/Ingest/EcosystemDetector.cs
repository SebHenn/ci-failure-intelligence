using System.Diagnostics;
using System.Text.RegularExpressions;
using CiFail.Core.Models;

namespace CiFail.Core.Ingest;

/// <summary>
/// Heuristically detects which ecosystem a log came from by scanning for characteristic
/// markers. Falls back to <see cref="Ecosystem.Generic"/> when no strong signal is found,
/// and respects an explicit caller override.
///
/// <para>
/// <b>Scoring counts markers, not occurrences.</b> Each marker contributes its weight at most
/// once however often it appears. The earlier version summed total match counts, which meant a
/// verbose tool emitting thirty <c>[ERROR] </c> lines outscored the handful of markers that
/// actually identified the ecosystem — a Python build with a chatty logger detected as Java.
/// </para>
///
/// <para>
/// Markers come in two weights. <b>Strong</b> ones are effectively unique to their ecosystem
/// (<c>npm ERR!</c>, <c>Cargo.toml</c>, <c>AndroidManifest</c>); <b>weak</b> ones are suggestive
/// but appear elsewhere too (<c>[ERROR] </c>, <c>gcc</c>, <c>gradlew</c>) and only matter in
/// aggregate. A single weak marker is not enough to claim an ecosystem — see
/// <see cref="MinimumScore"/>.
/// </para>
/// </summary>
public static class EcosystemDetector
{
    /// <summary>
    /// The canonical ecosystem names accepted by <c>--type</c>, pipe-separated.
    ///
    /// <para>
    /// A <c>const</c> so it can be embedded directly in a <c>[Description]</c> attribute as well
    /// as in the "unknown value" error — the help text used to be a hand-written list and had
    /// silently fallen four ecosystems behind. <see cref="TryParse"/> additionally accepts the
    /// obvious aliases (npm, mvn, cargo, xcode…).
    /// </para>
    /// </summary>
    public const string SupportedNamesText =
        "dotnet|node|python|java|go|rust|ruby|php|cpp|infra|swift|android|scala|elixir|generic";

    /// <summary>The same canonical names as a list, split from <see cref="SupportedNamesText"/>.</summary>
    public static readonly IReadOnlyList<string> SupportedNames = SupportedNamesText.Split('|');

    /// <summary>A marker effectively unique to its ecosystem.</summary>
    private const int StrongWeight = 3;

    /// <summary>A marker that merely suggests its ecosystem and shows up in other logs too.</summary>
    private const int WeakWeight = 1;

    /// <summary>
    /// The score an ecosystem needs before it beats <see cref="Ecosystem.Generic"/>: one strong
    /// marker, or two weak ones. A lone weak marker (one stray <c>gcc</c> in a Node log) is noise,
    /// and claiming an ecosystem from it only narrows the rule set to the wrong one.
    /// </summary>
    private const int MinimumScore = 2;

    /// <summary>Untrusted input; a pathological log must not hang the analysis.</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Ceiling on the time <see cref="Rank"/> may spend across <i>all</i> markers.
    ///
    /// <para>
    /// <see cref="MatchTimeout"/> alone bounds each marker, but there are ~130 of them and every
    /// one is evaluated on every call, so the per-marker limit multiplied out to a worst case of
    /// over two minutes before anything else in the pipeline had run. Detection is a heuristic:
    /// once the budget is gone, the markers not yet scored are treated as absent, which at worst
    /// costs us the ecosystem and falls back to <see cref="Ecosystem.Generic"/>.
    /// </para>
    /// </summary>
    private static readonly TimeSpan RankBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Tie-break order, most specific first. Previously a tie fell through to whatever order the
    /// score dictionary happened to enumerate in, which is not a decision.
    ///
    /// <para>
    /// The ordering principle is "how likely is this ecosystem's marker set to fire on someone
    /// else's log". Android outranks Java because every Android log is also a Gradle/Java log.
    /// Infra and C++ come last for the same reason in reverse: nearly every project builds a
    /// Docker image somewhere in CI, and native-extension builds drag <c>gcc</c>/<c>ld</c> into
    /// Python, Ruby and Node logs.
    /// </para>
    /// </summary>
    private static readonly Ecosystem[] Precedence =
    {
        Ecosystem.Android,
        Ecosystem.Swift,
        Ecosystem.Rust,
        Ecosystem.Go,
        Ecosystem.Dotnet,
        Ecosystem.Php,
        Ecosystem.Ruby,
        Ecosystem.Elixir,
        Ecosystem.Python,
        Ecosystem.Node,
        // Above Java, for the same reason Android is: sbt prefixes every line with `[error] `,
        // and the detector matches case-insensitively, so Maven's `[ERROR] ` weak marker fires
        // on every Scala build. A Scala log is a JVM log; a Java log is not a Scala one.
        Ecosystem.Scala,
        Ecosystem.Java,
        Ecosystem.Cpp,
        Ecosystem.Infra,
    };

    /// <summary>Detect the ecosystem, or honour an explicit <paramref name="overrideType"/>.</summary>
    public static Ecosystem Detect(LogDocument log, string? overrideType = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideType) && TryParse(overrideType, out var forced))
            return forced;

        var ranked = Rank(log);
        return ranked.Count > 0 && ranked[0].Score >= MinimumScore ? ranked[0].Ecosystem : Ecosystem.Generic;
    }

    /// <summary>
    /// Every ecosystem's score for this log, best first, ties broken by <see cref="Precedence"/>.
    /// Exposed because "why did it think this was Java?" is otherwise unanswerable, and because
    /// it makes the tie-breaking directly testable.
    /// </summary>
    public static IReadOnlyList<(Ecosystem Ecosystem, int Score)> Rank(LogDocument log)
    {
        var text = log.NormalizedText;

        // One budget shared across every ecosystem's markers, so the total cost of detection is
        // bounded rather than (marker count × per-marker timeout). Materialized eagerly — a lazy
        // Select would leave the stopwatch running across the caller's enumeration.
        var deadline = Stopwatch.StartNew();

        // Rank by score, then by precedence position — OrderBy is stable, so scoring in
        // precedence order and sorting only on the score would work too; being explicit keeps
        // the tie-break from depending on a sort's stability guarantee.
        return Precedence
            .Select((eco, index) =>
                (Ecosystem: eco, Score: Score(Markers[eco], text, deadline), Index: index))
            .ToList()
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Select(x => (x.Ecosystem, x.Score))
            .ToList();
    }

    public static bool TryParse(string value, out Ecosystem ecosystem)
    {
        ecosystem = value.Trim().ToLowerInvariant() switch
        {
            "dotnet" or "net" or ".net" or "csharp" or "nuget" or "msbuild" => Ecosystem.Dotnet,
            "node" or "npm" or "js" or "javascript" or "yarn" or "pnpm" => Ecosystem.Node,
            "python" or "py" or "pip" => Ecosystem.Python,
            // Kotlin resolves to Java on purpose: a Kotlin CI log is a Gradle log, and
            // java.yaml carries the Kotlin compiler rules.
            "java" or "maven" or "mvn" or "gradle" or "kotlin" or "kt" or "jvm" => Ecosystem.Java,
            "go" or "golang" => Ecosystem.Go,
            "rust" or "cargo" or "rs" => Ecosystem.Rust,
            "ruby" or "rb" or "bundler" or "gem" or "rails" => Ecosystem.Ruby,
            "php" or "composer" or "phpunit" => Ecosystem.Php,
            "cpp" or "c++" or "c" or "gcc" or "clang" or "g++" or "cmake" or "make" => Ecosystem.Cpp,
            "infra" or "docker" or "dockerfile" or "terraform" or "tf" or "tofu" or "opentofu"
                or "k8s" or "kubernetes" or "kubectl" or "helm" => Ecosystem.Infra,
            "scala" or "sbt" => Ecosystem.Scala,
            "elixir" or "ex" or "mix" or "erlang" => Ecosystem.Elixir,
            "swift" or "xcode" or "xcodebuild" or "ios" => Ecosystem.Swift,
            "android" or "aapt" => Ecosystem.Android,
            "generic" or "ci" => Ecosystem.Generic,
            _ => Ecosystem.Unknown,
        };
        return ecosystem != Ecosystem.Unknown;
    }

    /// <summary>Sum the weights of the markers that fire — each one counted once, however often it occurs.</summary>
    private static int Score(Marker[] markers, string text, Stopwatch deadline)
    {
        var total = 0;
        foreach (var marker in markers)
        {
            // Out of budget: every remaining marker counts as absent (see RankBudget).
            if (deadline.Elapsed > RankBudget) break;

            try
            {
                if (marker.Pattern.IsMatch(text)) total += marker.Weight;
            }
            catch (RegexMatchTimeoutException)
            {
                // Treat a marker that couldn't be evaluated in time as absent; detection is a
                // heuristic and must never be the thing that fails an analysis.
            }
        }
        return total;
    }

    private readonly record struct Marker(Regex Pattern, int Weight);

    private static Marker Strong(string pattern) => new(Compile(pattern), StrongWeight);
    private static Marker Weak(string pattern) => new(Compile(pattern), WeakWeight);

    /// <summary>Multiline so a marker can anchor to the start of a log line (<c>^denied: </c>).</summary>
    private static Regex Compile(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline, MatchTimeout);

    private static readonly Dictionary<Ecosystem, Marker[]> Markers = new()
    {
        [Ecosystem.Dotnet] = new[]
        {
            Strong(@"(?:error|warning) (?:NU|MSB|CS|NETSDK)\d{3,5}"),
            Strong(@"\.csproj"),
            Strong(@"\bdotnet (?:build|test|restore|publish|run|pack|tool)\b"),
            Weak(@"\bnuget\b"),
            Weak(@"\bmsbuild\b"),
            Weak(@"\.sln\b"),
        },

        [Ecosystem.Node] = new[]
        {
            Strong(@"npm ERR!"),
            Strong(@"\bERESOLVE\b"),
            Strong(@"Cannot find module"),
            Strong(@"node_modules"),
            Strong(@"package-lock\.json"),
            Strong(@"yarn\.lock"),
            Strong(@"pnpm-lock\.yaml"),
            // An invoked command, like the `go build` / `dotnet build` markers. A bare `npm`
            // or `yarn` stays weak (it turns up in prose), but nobody writes `yarn install`
            // about anything else — and a yarn/pnpm log need never mention its own lockfile,
            // which is how a real `--frozen-lockfile` failure scored 1 and fell back to generic.
            Strong(@"\bnpm (?:install|ci|run|test|publish|exec|audit|pack)\b"),
            Strong(@"\byarn (?:install|add|run|test|why|workspaces|dlx)\b"),
            Strong(@"\bpnpm (?:install|add|run|test|dlx|exec)\b"),
            Strong(@"\bnpx\b"),
            Weak(@"\byarn\b"),
            Weak(@"\bpnpm\b"),
            Weak(@"\bnpm\b"),
        },

        [Ecosystem.Python] = new[]
        {
            Strong(@"Traceback \(most recent call last\)"),
            Strong(@"ModuleNotFoundError"),
            Strong(@"ResolutionImpossible"),
            Strong(@"\bpip3? install\b"),
            Strong(@"requirements\.txt"),
            Strong(@"ERROR: Could not find a version that satisfies"),
            Strong(@"\.py"", line \d+"),
            // The pip/pytest markers above missed all of modern Python tooling, so a mypy,
            // ruff or poetry log scored 1 on a bare `.py` and fell back to generic. Each of
            // these names is effectively unique to Python — `black` is deliberately absent,
            // being an ordinary English word.
            Strong(@"\bpyproject\.toml\b"),
            Strong(@"\bpoetry\.lock\b"),
            Strong(@"\bmypy\b"),
            Strong(@"\bruff\b"),
            Strong(@"\b(?:flake8|pylint|pipenv|virtualenv)\b"),
            Strong(@"\b(?:poetry|uv|pipenv|tox) (?:install|lock|add|run|sync|-r|-e)\b"),
            Weak(@"\bpytest\b"),
            Weak(@"\bpython3?\b"),
            Weak(@"\.py\b"),
        },

        [Ecosystem.Java] = new[]
        {
            Strong(@"\bBUILD FAILURE\b"),
            Strong(@"\bpom\.xml\b"),
            Strong(@"\bjavac\b"),
            Strong(@"at org\.junit"),
            Strong(@"\bmaven\b"),
            Strong(@"\.java:"),
            Strong(@"Exception in thread ""[^""]*"" java\."),
            // Maven says BUILD FAILURE, Gradle says BUILD FAILED — only the first was here, so
            // a Gradle-only log had nothing strong in it at all. Anchored and without the
            // asterisks so it can't match Swift's `** BUILD FAILED **`.
            Strong(@"^BUILD (?:FAILED|SUCCESSFUL) in \d"),
            // Kotlin: the compiler's `e:` diagnostic prefix and .kt/.kts source references.
            // Kotlin rules live in java.yaml (see the pack), so they must resolve here.
            Strong(@"^e: (?:file://)?\S+\.kts?:"),
            Strong(@"\.kts?:\d+:\d+"),
            Weak(@"\bkotlin\b"),
            // Deliberately weak: `[ERROR] ` is a Maven convention that a dozen unrelated tools
            // also use, and gradlew is just as likely to be an Android build.
            Weak(@"\[ERROR\] "),
            Weak(@"\bgradlew\b"),
            Weak(@"\bgradle\b"),
            Weak(@"\.gradle\b"),
            Weak(@"OutOfMemoryError"),
        },

        [Ecosystem.Go] = new[]
        {
            Strong(@"\bgo\.mod\b"),
            Strong(@"\bgo\.sum\b"),
            Strong(@"\bgo (?:build|test|run|get|mod|vet|install)\b"),
            Strong(@"cannot find package"),
            Strong(@"\bgolang\b"),
            Strong(@"\.go:\d+"),
            Weak(@"undefined: "),
        },

        [Ecosystem.Rust] = new[]
        {
            Strong(@"error\[E\d{2,4}\]"),
            Strong(@"Cargo\.toml"),
            Strong(@"Cargo\.lock"),
            Strong(@"\brustc\b"),
            Strong(@"\bcargo\b"),
            Strong(@"could not compile"),
            Weak(@"--> src/"),
            Weak(@"\.rs:\d+"),
        },

        [Ecosystem.Ruby] = new[]
        {
            Strong(@"\bGemfile(?:\.lock)?\b"),
            Strong(@"\bbundler?\b"),
            Strong(@"\brspec\b"),
            Strong(@"\.rb:\d+:in "),
            Strong(@"\brake aborted\b"),
            Strong(@"\bLoadError\b"),
            // A bare `gem` matches ordinary prose ("a hidden gem"), so require a verb.
            Weak(@"\bgem (?:install|not found|""[^""]+"")"),
            Weak(@"\brake\b"),
        },

        [Ecosystem.Php] = new[]
        {
            Strong(@"PHP (?:Fatal|Parse|Warning) error"),
            Strong(@"\bPHPUnit\b"),
            Strong(@"\bcomposer\b"),
            Strong(@"vendor/autoload"),
            Strong(@"Fatal error: Uncaught"),
            Strong(@"\bpsr-4\b"),
            // Scoped to a file:line reference: a bare `.php` matches any mention of a filename.
            Weak(@"\.php(?::\d+|\b on line)"),
            Weak(@"Call to undefined"),
        },

        [Ecosystem.Cpp] = new[]
        {
            Strong(@"undefined reference to"),
            Strong(@"CMakeLists\.txt"),
            Strong(@"\bcmake\b"),
            Strong(@"No rule to make target"),
            Strong(@"collect2:"),
            Strong(@"fatal error: [^\n]*\.h(?:pp)?: No such file"),
            Weak(@"\.(?:cpp|cc|cxx|hpp)\b"),
            Weak(@"\bg\+\+\b"),
            Weak(@"\bgcc\b"),
            Weak(@"\bclang\b"),
            Weak(@"\bld: "),
        },

        [Ecosystem.Infra] = new[]
        {
            Strong(@"docker build"),
            Strong(@"failed to solve"),
            Strong(@"\bDockerfile\b"),
            Strong(@"\bterraform\b"),
            Strong(@"\btofu\b"),
            Strong(@"Error acquiring the state lock"),
            Strong(@"\bbuildkit\b"),
            Strong(@"Step \d+/\d+"),
            // Kubernetes/Helm belong here with Docker and Terraform: it is the deploy bucket,
            // and a `kubectl rollout` or `helm upgrade` failure previously scored 0.
            Strong(@"\bkubectl\b"),
            Strong(@"\bkubelet\b"),
            Strong(@"\bkubernetes\b"),
            Strong(@"\bhelm (?:upgrade|install|template|uninstall|rollback|lint|dependency)\b"),
            Strong(@"\b(?:CrashLoopBackOff|ImagePullBackOff|ErrImagePull|ErrImageNeverPull)\b"),
            Weak(@"\bhelm\b"),
            Weak(@"\bk8s\b"),
            // `denied: ` used to live here and matched every "permission denied:" in every log.
            Weak(@"\bunauthorized: "),
            Weak(@"^denied: "),
        },

        [Ecosystem.Swift] = new[]
        {
            Strong(@"\bswiftc\b"),
            Strong(@"\bxcodebuild\b"),
            Strong(@"no such module"),
            Strong(@"Provisioning profile"),
            Strong(@"\.swift:\d+"),
            Strong(@"\*\* BUILD FAILED \*\*"),
            Strong(@"CompileSwift"),
            // SwiftPM and CocoaPads builds need never mention xcodebuild or a .swift line
            // number, so `swift build` alone used to score 0 and fall back to generic.
            Strong(@"\bswift (?:build|test|package|run)\b"),
            Strong(@"Package\.swift"),
            Strong(@"\bPodfile(?:\.lock)?\b"),
            Strong(@"\bxcrun\b"),
            Weak(@"\bXcode\b"),
            Weak(@"Code Sign(?:ing)?"),
        },

        [Ecosystem.Scala] = new[]
        {
            Strong(@"\bbuild\.sbt\b"),
            Strong(@"\bsbt (?:compile|test|run|clean|assembly|publish)\b"),
            Strong(@"\bproject/build\.properties\b"),
            Strong(@"\.scala:\d+"),
            Strong(@"\bscalac\b"),
            Strong(@"\bScalaTest\b"),
            Strong(@"\bcrossScalaVersions\b"),
            Weak(@"\bsbt\b"),
            Weak(@"\bscala\b"),
        },

        [Ecosystem.Elixir] = new[]
        {
            Strong(@"\bmix\.exs\b"),
            Strong(@"\bmix (?:compile|test|deps\.get|deps\.compile|release|format)\b"),
            Strong(@"\.exs?:\d+"),
            Strong(@"\bmix\.lock\b"),
            Strong(@"\*\* \((?:CompileError|SyntaxError|UndefinedFunctionError|Mix\.Error)\)"),
            Strong(@"\bExUnit\b"),
            Weak(@"\belixir\b"),
            Weak(@"\bhex\b"),
        },

        [Ecosystem.Android] = new[]
        {
            Strong(@"\baapt2?\b"),
            Strong(@"AndroidManifest"),
            Strong(@"com\.android"),
            Strong(@"Execution failed for task ':app"),
            Strong(@"SDK location"),
            Strong(@"Android resource"),
            Strong(@"\bdexing\b"),
            Strong(@":app:"),
            Weak(@"lint(?:Debug|Release)"),
            // Shared with Java on purpose: an Android build *is* a Gradle build, so this can
            // only ever be corroborating evidence, never the deciding vote.
            Weak(@"\bgradlew\b"),
        },
    };
}
