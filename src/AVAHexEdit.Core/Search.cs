using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace AVAHexEdit.Core;

/// <summary>What the find box holds: hex bytes, text, or a number written out as bytes.</summary>
public enum FindMode { Hex, Text, Utf16, Int16, Int32, Int64, Float, Double }

/// <summary>
/// What to look for, as bytes: <see cref="Wild"/> marks bytes that match anything (?? in hex), and with
/// <see cref="IgnoreCase"/> the letters A to Z match either case.
/// </summary>
public sealed record Pattern(byte[] Bytes, bool[]? Wild = null, bool IgnoreCase = false)
{
    public int Length => Bytes.Length;

    /// <summary>
    /// Reads what was typed into the find box. Throws <see cref="FormatException"/> with a message to show when it
    /// isn't what <paramref name="mode"/> expects.
    /// </summary>
    public static Pattern Parse(string text, FindMode mode, bool matchCase = true, bool bigEndian = false)
    {
        switch (mode)
        {
            case FindMode.Hex:
                var (bytes, wild) = ByteFormats.ParseHex(text, allowWild: true);
                if (bytes.Length == 0) throw new FormatException("Type the bytes to find in hex, such as 4D 5A 90 00 (?? matches any byte).");
                return new Pattern(bytes, wild.Any(w => w) ? wild : null);
            case FindMode.Text:
            case FindMode.Utf16:
                var unescaped = ByteFormats.Unescape(text);
                if (unescaped.Length == 0) throw new FormatException("Type the text to find.");
                return new Pattern((mode == FindMode.Text ? Encoding.UTF8 : Encoding.Unicode).GetBytes(unescaped), null, !matchCase);
            default:
                return new Pattern(Number(text, mode, bigEndian));
        }
    }

    /// <summary>A number typed in decimal (or 0x hex) as the bytes it is stored as.</summary>
    public static byte[] Number(string text, FindMode mode, bool bigEndian)
    {
        var t = text.Trim().Replace("_", "", StringComparison.Ordinal);
        if (t.Length == 0) throw new FormatException("Type a number to find.");
        var bytes = new byte[mode switch { FindMode.Int16 => 2, FindMode.Int32 or FindMode.Float => 4, _ => 8 }];
        if (mode is FindMode.Float or FindMode.Double)
        {
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) throw new FormatException($"\"{text}\" isn't a number.");
            if (mode == FindMode.Float)
            {
                if (bigEndian) BinaryPrimitives.WriteSingleBigEndian(bytes, (float)d); else BinaryPrimitives.WriteSingleLittleEndian(bytes, (float)d);
            }
            else if (bigEndian) BinaryPrimitives.WriteDoubleBigEndian(bytes, d); else BinaryPrimitives.WriteDoubleLittleEndian(bytes, d);
            return bytes;
        }
        if (!TryInteger(t, out var value)) throw new FormatException($"\"{text}\" isn't a whole number.");
        var bits = bytes.Length * 8;
        if (bits < 64)
        {
            var min = -(1L << (bits - 1));
            var max = (1L << bits) - 1;
            if (value < min || value > max) throw new FormatException($"{text} doesn't fit in {bits} bits.");
        }
        var u = (ulong)value;
        for (var i = 0; i < bytes.Length; i++)
            bytes[bigEndian ? bytes.Length - 1 - i : i] = (byte)(u >> (8 * i));
        return bytes;
    }

    private static bool TryInteger(string t, out long value)
    {
        var negative = t.StartsWith('-');
        var digits = negative ? t[1..] : t;
        bool ok;
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            ok = ulong.TryParse(digits[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var u);
            value = (long)u;
        }
        else
        {
            ok = ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var u);
            value = (long)u;
        }
        if (negative) value = -value;
        return ok;
    }
}

/// <summary>Finds a <see cref="Pattern"/>, reading a block at a time so a file of any size can be searched.</summary>
public static class Finder
{
    private const int Block = 1 << 20;

    /// <summary>
    /// The first match at or after <paramref name="from"/> (or, going back, the last one that starts before it), or -1.
    /// </summary>
    public static long Find(IBytes data, Pattern pattern, long from, bool forward = true, CancellationToken cancel = default) =>
        forward ? Forward(data, pattern, Math.Max(0, from), data.Length, cancel) : Backward(data, pattern, Math.Min(from, data.Length), cancel);

    /// <summary>Every match, not overlapping, up to <paramref name="limit"/> of them.</summary>
    public static List<long> FindAll(IBytes data, Pattern pattern, int limit = 1_000_000, CancellationToken cancel = default)
    {
        var found = new List<long>();
        for (var at = Forward(data, pattern, 0, data.Length, cancel); at >= 0 && found.Count < limit; at = Forward(data, pattern, at + pattern.Length, data.Length, cancel))
            found.Add(at);
        return found;
    }

    private static long Forward(IBytes data, Pattern p, long from, long to, CancellationToken cancel)
    {
        var n = p.Length;
        if (n == 0 || from + n > to) return -1;
        var buffer = new byte[Block + n];
        for (var start = from; start + n <= to; start += Block)
        {
            cancel.ThrowIfCancellationRequested();
            var got = data.Read(start, buffer.AsSpan(0, (int)Math.Min(buffer.Length, to - start)));
            var span = buffer.AsSpan(0, got);
            var i = IndexOf(span, p);
            if (i >= 0) return start + i;
            if (got < buffer.Length) break;
        }
        return -1;
    }

    private static long Backward(IBytes data, Pattern p, long before, CancellationToken cancel)
    {
        var n = p.Length;
        if (n == 0) return -1;
        // Matches must start before `before`, so they end by before + n - 1.
        var end = Math.Min(data.Length, before + n - 1);
        var buffer = new byte[Block + n];
        while (end - n >= 0)
        {
            cancel.ThrowIfCancellationRequested();
            var start = Math.Max(0, end - buffer.Length);
            var got = data.Read(start, buffer.AsSpan(0, (int)(end - start)));
            var i = LastIndexOf(buffer.AsSpan(0, got), p);
            if (i >= 0) return start + i;
            if (start == 0) break;
            end = start + n - 1;
        }
        return -1;
    }

    private static int IndexOf(ReadOnlySpan<byte> span, Pattern p)
    {
        if (p.Wild is null && !p.IgnoreCase) return span.IndexOf(p.Bytes);
        for (var i = 0; i + p.Length <= span.Length; i++)
            if (Matches(span[i..], p)) return i;
        return -1;
    }

    private static int LastIndexOf(ReadOnlySpan<byte> span, Pattern p)
    {
        if (p.Wild is null && !p.IgnoreCase) return span.LastIndexOf(p.Bytes);
        for (var i = span.Length - p.Length; i >= 0; i--)
            if (Matches(span[i..], p)) return i;
        return -1;
    }

    /// <summary>Whether <paramref name="span"/> starts with the pattern.</summary>
    public static bool Matches(ReadOnlySpan<byte> span, Pattern p)
    {
        if (span.Length < p.Length) return false;
        for (var j = 0; j < p.Length; j++)
        {
            if (p.Wild is { } w && w[j]) continue;
            var a = span[j];
            var b = p.Bytes[j];
            if (a == b) continue;
            if (p.IgnoreCase && Fold(a) == Fold(b)) continue;
            return false;
        }
        return true;
    }

    private static byte Fold(byte b) => b is >= (byte)'a' and <= (byte)'z' ? (byte)(b - 32) : b;
}
