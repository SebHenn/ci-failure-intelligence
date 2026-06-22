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
        EcosystemDetector.Detect(Doc("npm ERR! boom"), "not-a-real-type")
            .Should().Be(Ecosystem.Node);
    }
}
