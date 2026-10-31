using System.Globalization;
using System.Text;

namespace AVAHexEdit.Core;

/// <summary>One way of reading the bytes at the caret, and what they come to.</summary>
public readonly record struct Reading(string Name, string Value);

/// <summary>
/// The bytes at the caret read every common way: whole numbers of each size signed and unsigned, floating point,
/// characters, times and a GUID, in the byte order chosen. A dash where there aren't enough bytes left.
/// </summary>
public static class Inspector
{
    /// <summary>How many bytes to give <see cref="Read"/>: the most any reading needs.</summary>
    public const int Needs = 16;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static List<Reading> Read(ReadOnlySpan<byte> b, bool bigEndian)
    {
        const string none = "—";
        var r = new List<Reading>();
        var bytes = b.ToArray();
        void Add(string name, int size, Func<string> value) => r.Add(new Reading(name, bytes.Length >= size ? value() : none));

        ulong U(int size)
        {
            ulong v = 0;
            for (var i = 0; i < size; i++)
                v |= (ulong)bytes[bigEndian ? size - 1 - i : i] << (8 * i);
            return v;
        }
        long S(int size)
        {
            var shift = 64 - size * 8;
            return (long)(U(size) << shift) >> shift;
        }

        Add("Binary", 1, () => Convert.ToString(bytes[0], 2).PadLeft(8, '0').Insert(4, " "));
        Add("Int8", 1, () => ((sbyte)bytes[0]).ToString(Inv));
        Add("UInt8", 1, () => bytes[0].ToString(Inv));
        Add("Int16", 2, () => S(2).ToString(Inv));
        Add("UInt16", 2, () => U(2).ToString(Inv));
        Add("Int24", 3, () => S(3).ToString(Inv));
        Add("UInt24", 3, () => U(3).ToString(Inv));
        Add("Int32", 4, () => S(4).ToString(Inv));
        Add("UInt32", 4, () => U(4).ToString(Inv));
        Add("Int64", 8, () => S(8).ToString(Inv));
        Add("UInt64", 8, () => U(8).ToString(Inv));
        Add("Half", 2, () => Float(BitConverter.UInt16BitsToHalf((ushort)U(2))));
        Add("Float", 4, () => Float(BitConverter.UInt32BitsToSingle((uint)U(4))));
        Add("Double", 8, () => Float(BitConverter.UInt64BitsToDouble(U(8))));
        Add("LEB128", 1, () => Leb128(bytes));
        Add("Character", 1, () => Character(bytes[0]));
        Add("UTF-8", 1, () => Utf8(bytes));
        Add("UTF-16", 2, () => Utf16((char)U(2)));
        Add("Unix time", 4, () => UnixTime((long)U(4)));
        Add("Unix time (64)", 8, () => UnixTime(S(8)));
        Add("FILETIME", 8, () => FileTime(U(8)));
        Add("DOS date-time", 4, () => DosTime((uint)U(4)));
        Add("GUID", 16, () => new Guid(bytes.AsSpan(0, 16), bigEndian).ToString("B").ToUpperInvariant());
        return r;
    }

    private static string Float<T>(T v) where T : System.Numerics.IFloatingPoint<T> => v.ToString("G9", Inv);

    private static string Character(byte b) => b switch
    {
        0 => "NUL",
        0x09 => "TAB",
        0x0A => "LF",
        0x0D => "CR",
        0x1B => "ESC",
        0x20 => "space",
        0x7F => "DEL",
        < 0x20 => $"control (^{(char)(b + 64)})",
        < 0x7F => $"'{(char)b}'",
        < 0xA0 => "—",
        _ => $"'{(char)b}' (Latin-1)",
    };

    private static string Utf8(byte[] bytes)
    {
        var status = Rune.DecodeFromUtf8(bytes, out var rune, out var used);
        if (status != System.Buffers.OperationStatus.Done) return "not UTF-8 here";
        return $"{Describe(rune)} ({used} byte{(used == 1 ? "" : "s")})";
    }

    private static string Utf16(char c) => char.IsSurrogate(c) ? $"U+{(int)c:X4} (half a pair)" : Describe(new Rune(c));

    private static string Describe(Rune r) => Rune.IsControl(r) || Rune.IsWhiteSpace(r)
        ? $"U+{r.Value:X4}"
        : $"'{r}' U+{r.Value:X4}";

    private static string Leb128(byte[] bytes)
    {
        ulong v = 0;
        for (var i = 0; i < Math.Min(bytes.Length, 10); i++)
        {
            v |= (ulong)(bytes[i] & 0x7F) << (7 * i);
            if ((bytes[i] & 0x80) == 0) return $"{v.ToString(Inv)} ({i + 1} byte{(i == 0 ? "" : "s")})";
        }
        return "—";
    }

    private static string UnixTime(long seconds) =>
        seconds is >= -62135596800 and <= 253402300799
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", Inv)
            : "out of range";

    private static string FileTime(ulong ticks) =>
        ticks <= (ulong)DateTime.MaxValue.ToFileTimeUtc()
            ? DateTime.FromFileTimeUtc((long)ticks).ToString("yyyy-MM-dd HH:mm:ss.fffffff 'UTC'", Inv)
            : "out of range";

    /// <summary>MS-DOS packs a time into the low 16 bits and the date into the high 16 (as in ZIP and FAT).</summary>
    private static string DosTime(uint v)
    {
        var time = v & 0xFFFF;
        var date = v >> 16;
        int day = (int)(date & 31), month = (int)((date >> 5) & 15), year = 1980 + (int)(date >> 9);
        int second = (int)(time & 31) * 2, minute = (int)((time >> 5) & 63), hour = (int)(time >> 11);
        if (day is < 1 or > 31 || month is < 1 or > 12 || hour > 23 || minute > 59 || second > 59 || day > DateTime.DaysInMonth(year, month))
            return "not a date";
        return new DateTime(year, month, day, hour, minute, second).ToString("yyyy-MM-dd HH:mm:ss", Inv);
    }
}

/// <summary>Turns a number into bytes in the chosen order, for writing a value at the caret.</summary>
public static class Writing
{
    /// <summary>
    /// The bytes for <paramref name="text"/> read as the inspector's <paramref name="name"/> (Int8 to Double), or
    /// throws <see cref="FormatException"/>.
    /// </summary>
    public static byte[] Bytes(string name, string text, bool bigEndian)
    {
        var t = text.Trim();
        var (size, signed, isFloat) = name switch
        {
            "Int8" => (1, true, false), "UInt8" => (1, false, false),
            "Int16" => (2, true, false), "UInt16" => (2, false, false),
            "Int24" => (3, true, false), "UInt24" => (3, false, false),
            "Int32" => (4, true, false), "UInt32" => (4, false, false),
            "Int64" => (8, true, false), "UInt64" => (8, false, false),
            "Half" => (2, true, true), "Float" => (4, true, true), "Double" => (8, true, true),
            _ => throw new FormatException($"{name} can't be written here."),
        };
        var bytes = new byte[size];
        ulong bits;
        if (isFloat)
        {
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) throw new FormatException($"\"{text}\" isn't a number.");
            bits = size switch
            {
                2 => BitConverter.HalfToUInt16Bits((Half)d),
                4 => BitConverter.SingleToUInt32Bits((float)d),
                _ => BitConverter.DoubleToUInt64Bits(d),
            };
        }
        else
        {
            var hex = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (signed && !hex)
            {
                if (!long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var s)) throw new FormatException($"\"{text}\" isn't a whole number.");
                var min = size == 8 ? long.MinValue : -(1L << (size * 8 - 1));
                var max = size == 8 ? long.MaxValue : (1L << (size * 8 - 1)) - 1;
                if (s < min || s > max) throw new FormatException($"{text} doesn't fit in {name}.");
                bits = (ulong)s;
            }
            else
            {
                if (!ulong.TryParse(hex ? t[2..] : t, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var u))
                    throw new FormatException($"\"{text}\" isn't a whole number{(signed ? "" : " of 0 or more")}.");
                if (size < 8 && u >> (size * 8) != 0) throw new FormatException($"{text} doesn't fit in {name}.");
                bits = u;
            }
        }
        for (var i = 0; i < size; i++)
            bytes[bigEndian ? size - 1 - i : i] = (byte)(bits >> (8 * i));
        return bytes;
    }

    /// <summary>The readings that can be written back.</summary>
    public static bool CanWrite(string name) => name is "Int8" or "UInt8" or "Int16" or "UInt16" or "Int24" or "UInt24"
        or "Int32" or "UInt32" or "Int64" or "UInt64" or "Half" or "Float" or "Double";
}
