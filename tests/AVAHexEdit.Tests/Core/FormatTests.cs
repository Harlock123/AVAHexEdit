using AVAHexEdit.Core;

namespace AVAHexEdit.Tests.Core;

public class FormatTests
{
    private static string Value(List<Reading> r, string name) => r.Single(x => x.Name == name).Value;

    [Fact]
    public void The_inspector_reads_the_bytes_every_way_in_either_order()
    {
        byte[] b = [0xD2, 0x04, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F, 0, 0, 0, 0, 0, 0, 0, 0];
        var le = Inspector.Read(b, bigEndian: false);
        Assert.Equal("1101 0010", Value(le, "Binary"));
        Assert.Equal("-46", Value(le, "Int8"));
        Assert.Equal("210", Value(le, "UInt8"));
        Assert.Equal("1234", Value(le, "Int16"));
        Assert.Equal("1234", Value(le, "UInt32"));
        Assert.Equal("4607182418800018642", Value(le, "Int64"));
        Assert.Equal("1970-01-01 00:20:34 UTC", Value(le, "Unix time"));
        var be = Inspector.Read(b, bigEndian: true);
        Assert.Equal("-11772", Value(be, "Int16"));
        Assert.Equal("53764", Value(be, "UInt16"));

        var one = Inspector.Read([0x00, 0x00, 0x80, 0x3F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F], false);
        Assert.Equal("1", Value(one, "Float"));
        Assert.Equal("—", Value(one, "GUID")); // not enough bytes
        Assert.Equal("'é' U+00E9 (2 bytes)", Value(Inspector.Read([0xC3, 0xA9], false), "UTF-8"));
        Assert.Equal("624485 (3 bytes)", Value(Inspector.Read([0xE5, 0x8E, 0x26], false), "LEB128"));
        Assert.Equal("LF", Value(Inspector.Read([0x0A], false), "Character"));
        // ZIP's packed DOS time: 2026-10-31 12:34:56.
        var dos = (uint)(((2026 - 1980) << 25) | (10 << 21) | (31 << 16) | (12 << 11) | (34 << 5) | 28);
        Assert.Equal("2026-10-31 12:34:56", Value(Inspector.Read(BitConverter.GetBytes(dos), false), "DOS date-time"));
    }

    [Fact]
    public void Values_written_back_become_the_right_bytes()
    {
        Assert.Equal([0xD2, 0x04], Writing.Bytes("Int16", "1234", false));
        Assert.Equal([0x04, 0xD2], Writing.Bytes("UInt16", "0x4D2", true));
        Assert.Equal([0xFF], Writing.Bytes("Int8", "-1", false));
        Assert.Equal([0x00, 0x00, 0x80, 0x3F], Writing.Bytes("Float", "1", false));
        Assert.Throws<FormatException>(() => Writing.Bytes("UInt8", "256", false));
        Assert.Throws<FormatException>(() => Writing.Bytes("Int8", "-129", false));
        Assert.Throws<FormatException>(() => Writing.Bytes("UInt16", "-1", false));
        Assert.Throws<FormatException>(() => Writing.Bytes("GUID", "1", false));
    }

    [Fact]
    public void Checksums_match_the_standard_check_values()
    {
        using var d = Document.FromBytes("123456789"u8.ToArray());
        var c = Checksums.Compute(d, new ByteRange(0, d.Length));
        Assert.Equal("CBF43926", c[ChecksumKind.Crc32]);
        Assert.Equal("091E01DE", c[ChecksumKind.Adler32]);
        Assert.Equal("25f9e794323b453885f5181f1b624d0b", c[ChecksumKind.Md5]);
        Assert.Equal("f7c3bc1d808e04732adf679965ccc34ca7ae3441", c[ChecksumKind.Sha1]);
        Assert.Equal("15e2b0d3c33891ebb0f1ef609ec419420c20e320ce94c65fbc8c3312448eb225", c[ChecksumKind.Sha256]);
        // Just part of it: "345".
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData("345"u8)), Checksums.Compute(d, new ByteRange(2, 3))[ChecksumKind.Md5]);
    }

    [Fact]
    public void Comparing_lists_each_run_that_differs_and_the_extra_length()
    {
        using var a = Document.FromBytes([1, 2, 3, 4, 5, 6, 7, 8]);
        using var b = Document.FromBytes([1, 9, 9, 4, 5, 6, 0, 8, 10, 11]);
        var c = Comparison.Run(a, b);
        Assert.Equal([new ByteRange(1, 2), new ByteRange(6, 1), new ByteRange(8, 2)], c.Differences);
        Assert.Equal(5, c.DifferentBytes);
        Assert.Equal("3 differences, 5 bytes; the second file is 2 bytes longer.", c.Summary);
        Assert.True(Comparison.Run(a, a).Same);
        Assert.Equal("The files are the same.", Comparison.Run(a, a).Summary);

        // A difference running into the extra bytes joins them.
        using var c1 = Document.FromBytes([1, 2]);
        using var c2 = Document.FromBytes([1, 3, 4]);
        Assert.Equal([new ByteRange(1, 2)], Comparison.Run(c1, c2).Differences);

        // Across the 1 MB blocks it reads in.
        var big1 = new byte[3_000_000];
        var big2 = (byte[])big1.Clone();
        big2[1_048_575] = 1;
        big2[1_048_576] = 1;
        big2[2_999_999] = 1;
        using var d1 = Document.FromBytes(big1);
        using var d2 = Document.FromBytes(big2);
        Assert.Equal([new ByteRange(1_048_575, 2), new ByteRange(2_999_999, 1)], Comparison.Run(d1, d2).Differences);
    }

    [Fact]
    public void Hex_text_and_offsets_read_however_they_are_written()
    {
        Assert.Equal([0x4D, 0x5A, 0x90, 0x00], ByteFormats.ParseHex("4D5A9000").Bytes);
        Assert.Equal([0x4D, 0x5A], ByteFormats.ParseHex("0x4D, 0x5a").Bytes);
        Assert.Equal([0x4D, 0x5A], ByteFormats.ParseHex("\\x4D\\x5A").Bytes);
        Assert.Equal([0x01, 0x02], ByteFormats.ParseHex("{ 0x01, 0x02 }").Bytes);
        Assert.Equal([false, true], ByteFormats.ParseHex("4D ??", allowWild: true).Wild);
        Assert.Throws<FormatException>(() => ByteFormats.ParseHex("4D ??"));
        Assert.False(ByteFormats.TryParseHex("hello", out _));
        Assert.Equal("{\n    0x01, 0x02\n}", ByteFormats.ToCArray([1, 2]));
        Assert.Equal("A\n.", ByteFormats.ToText([0x41, 0x0A, 0x00], TextEncodingChoice.Ascii));
        Assert.Equal([0x41, 0x3F], ByteFormats.FromText("Aé", TextEncodingChoice.Ascii));
        Assert.Equal([0x41, 0xE9], ByteFormats.FromText("Aé", TextEncodingChoice.Latin1));
        Assert.Equal("a\nb\x01", ByteFormats.Unescape("a\\nb\\x01"));

        Assert.True(ByteFormats.TryParseOffset("1F40", 0, true, out var o) && o == 0x1F40);
        Assert.True(ByteFormats.TryParseOffset("1F40h", 0, false, out o) && o == 0x1F40);
        Assert.True(ByteFormats.TryParseOffset("0x10", 0, false, out o) && o == 16);
        Assert.True(ByteFormats.TryParseOffset("#100", 0, true, out o) && o == 100);
        Assert.True(ByteFormats.TryParseOffset("+10", 100, false, out o) && o == 110);
        Assert.True(ByteFormats.TryParseOffset("-0x10", 100, false, out o) && o == 84);
        Assert.False(ByteFormats.TryParseOffset("-200", 100, false, out _));
        Assert.False(ByteFormats.TryParseOffset("12G", 0, true, out _));
        Assert.Equal("12.4 KB", ByteFormats.Size(12_700));
        Assert.Equal("00001F40", ByteFormats.Offset(0x1F40, true));
    }
}
