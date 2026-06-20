using CiFail.Core.Ingest;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Ingest;

public class LogNormalizerTests
{
    [Fact]
    public void StripAnsi_removes_color_escape_codes()
    {
        var raw = "[31merror[0m: boom";
        LogNormalizer.StripAnsi(raw).Should().Be("error: boom");
    }

    [Fact]
    public void Normalize_drops_leading_iso_timestamp()
    {
        var raw = "2026-06-20T10:15:35.1110000Z error CS0103: nope";
        LogNormalizer.Normalize(raw).Should().Be("error CS0103: nope");
    }

    [Fact]
    public void Normalize_drops_leading_bracket_timestamp()
    {
        var raw = "[10:15:35] npm ERR! missing module";
        LogNormalizer.Normalize(raw).Should().Be("npm ERR! missing module");
    }

    [Fact]
    public void Normalize_collapses_crlf_and_trims_trailing_whitespace()
    {
        var raw = "line one   \r\nline two\t\r\n";
        LogNormalizer.Normalize(raw).Should().Be("line one\nline two");
    }

    [Fact]
    public void Scrub_collapses_volatile_tokens_so_signatures_match()
    {
        var a = LogNormalizer.Scrub(@"failed after 412 ms at C:\proj\App.csproj");
        var b = LogNormalizer.Scrub(@"failed after 999 ms at C:\other\App.csproj");
        a.Should().Be(b);
        a.Should().Contain("<n>").And.Contain("<path>");
    }
}
