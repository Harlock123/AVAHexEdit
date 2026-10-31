using System.Text;
using AVAHexEdit.Core;

namespace AVAHexEdit.Tests.Core;

public class SearchTests
{
    [Fact]
    public void Hex_with_wildcards_text_ignoring_case_and_numbers_in_either_byte_order()
    {
        using var d = Document.FromBytes([0x00, 0x4D, 0x5A, 0x90, 0x00, 0x4D, 0x5A, 0x50, 0x00, .. "Hello"u8, 0x39, 0x30, 0x00, 0x00]);
        Assert.Equal(1, Finder.Find(d, Pattern.Parse("4D 5A", FindMode.Hex), 0));
        Assert.Equal(5, Finder.Find(d, Pattern.Parse("4D5A", FindMode.Hex), 2));
        Assert.Equal(5, Finder.Find(d, Pattern.Parse("4D 5A ?? 00", FindMode.Hex), 2));
        Assert.Equal(1, Finder.Find(d, Pattern.Parse("4D 5A ?? 00", FindMode.Hex), 0));
        Assert.Equal(9, Finder.Find(d, Pattern.Parse("hello", FindMode.Text, matchCase: false), 0));
        Assert.Equal(-1, Finder.Find(d, Pattern.Parse("hello", FindMode.Text, matchCase: true), 0));
        Assert.Equal(14, Finder.Find(d, Pattern.Parse("12345", FindMode.Int32), 0));
        Assert.Equal([0x00, 0x00, 0x30, 0x39], Pattern.Parse("12345", FindMode.Int32, bigEndian: true).Bytes);
        Assert.Equal([0xFF, 0xFF], Pattern.Parse("-1", FindMode.Int16).Bytes);
        Assert.Equal([0x00, 0x00, 0x80, 0x3F], Pattern.Parse("1.0", FindMode.Float).Bytes);
        Assert.Throws<FormatException>(() => Pattern.Parse("70000", FindMode.Int16));
        Assert.Throws<FormatException>(() => Pattern.Parse("4G", FindMode.Hex));
        Assert.Equal(Encoding.Unicode.GetBytes("Hi"), Pattern.Parse("Hi", FindMode.Utf16).Bytes);
    }

    [Fact]
    public void Backwards_finds_the_last_match_before_the_caret()
    {
        using var d = Document.FromBytes("ab ab ab"u8.ToArray());
        var p = Pattern.Parse("ab", FindMode.Text);
        Assert.Equal(6, Finder.Find(d, p, d.Length, forward: false));
        Assert.Equal(3, Finder.Find(d, p, 6, forward: false));
        Assert.Equal(0, Finder.Find(d, p, 3, forward: false));
        Assert.Equal(-1, Finder.Find(d, p, 0, forward: false));
        Assert.Equal([0, 3, 6], Finder.FindAll(d, p));
    }

    [Fact]
    public void A_match_across_the_boundary_between_blocks_is_found_both_ways()
    {
        var bytes = new byte[(1 << 20) * 2 + 100];
        var at = (1 << 20) - 2;
        "NEEDLE"u8.CopyTo(bytes.AsSpan(at));
        "NEEDLE"u8.CopyTo(bytes.AsSpan(bytes.Length - 6));
        using var d = Document.FromBytes(bytes);
        var p = Pattern.Parse("needle", FindMode.Text, matchCase: false);
        Assert.Equal(at, Finder.Find(d, p, 0));
        Assert.Equal(bytes.Length - 6, Finder.Find(d, p, at + 1));
        Assert.Equal(at, Finder.Find(d, p, bytes.Length - 6, forward: false));
        Assert.Equal(at, Finder.Find(d, Pattern.Parse("4E 45 45 44 4C 45", FindMode.Hex), 0));
        Assert.Equal(bytes.Length - 6, Finder.Find(d, Pattern.Parse("4E 45 45 44 4C 45", FindMode.Hex), d.Length, forward: false));
    }
}
