using CiFail.Cli.Output;
using FluentAssertions;

namespace CiFail.Cli.Tests;

/// <summary>
/// Covers the glyph fallback. <see cref="Glyphs.Unicode"/> is decided once per process from the
/// console encoding, so these assert the invariants that must hold on whichever branch is live —
/// the failure mode being a legacy Windows code page turning "✓ 12 rules" into "? 12 rules".
/// </summary>
public sealed class GlyphsTests
{
    public static TheoryData<string> All => new()
    {
        Glyphs.Check, Glyphs.Cross, Glyphs.Warning, Glyphs.Bullet,
        Glyphs.Ellipsis, Glyphs.Dash, Glyphs.Times, Glyphs.Dot,
    };

    [Theory]
    [MemberData(nameof(All))]
    public void Every_glyph_renders_as_something(string glyph)
    {
        // An empty glyph would silently drop the marker from a line rather than degrade it.
        glyph.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [MemberData(nameof(All))]
    public void The_ascii_fallback_is_actually_ascii(string glyph)
    {
        if (Glyphs.Unicode) return; // this run has a capable console; nothing to check

        glyph.ToCharArray().Should().OnlyContain(c => c < 128);
    }

    [Fact]
    public void The_console_encoding_can_represent_whatever_was_chosen()
    {
        // The contract in both directions: if Unicode was chosen, the encoding must survive a
        // round-trip; if it wasn't, the ASCII fallbacks always do.
        var encoding = Console.OutputEncoding;
        var line = string.Concat(All.Select(row => (string)row[0]!));

        encoding.GetString(encoding.GetBytes(line)).Should().Be(line);
    }
}
