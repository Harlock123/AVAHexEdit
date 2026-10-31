using System.Globalization;
using System.Text;

namespace AVAHexEdit.Core;

/// <summary>How the text column shows bytes, and how typed text becomes bytes.</summary>
public enum TextEncodingChoice { Ascii, Latin1 }

/// <summary>Bytes written out as text, and text read back as bytes: hex, C arrays, Base64, escapes, offsets.</summary>
public static class ByteFormats
{
    /// <summary>"4D 5A 90 00".</summary>
    public static string ToHex(ReadOnlySpan<byte> bytes, string separator = " ")
    {
        var sb = new StringBuilder(bytes.Length * (2 + separator.Length));
        for (var i = 0; i < bytes.Length; i++)
        {
            if (i > 0) sb.Append(separator);
            sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>A C (and C#) array: <c>{ 0x4D, 0x5A, ... }</c>, sixteen to a line.</summary>
    public static string ToCArray(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder("{");
        for (var i = 0; i < bytes.Length; i++)
        {
            sb.Append(i % 16 == 0 ? "\n    " : " ");
            sb.Append("0x").Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            if (i < bytes.Length - 1) sb.Append(',');
        }
        return sb.Append("\n}").ToString();
    }

    public static string ToBase64(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes);

    /// <summary>The bytes as text, one character a byte, with anything unprintable as a dot (the text column).</summary>
    public static char Shown(byte b, TextEncodingChoice encoding) =>
        b is >= 0x20 and < 0x7F ? (char)b
        : encoding == TextEncodingChoice.Latin1 && b >= 0xA0 ? (char)b
        : '.';

    /// <summary>The bytes as text in the encoding, as copied from the text column (unprintable bytes as dots).</summary>
    public static string ToText(ReadOnlySpan<byte> bytes, TextEncodingChoice encoding)
    {
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++) chars[i] = bytes[i] is 0x0A or 0x0D or 0x09 ? (char)bytes[i] : Shown(bytes[i], encoding);
        return new string(chars);
    }

    /// <summary>Text typed or pasted into the text column as bytes: ASCII (anything else becomes ?) or Latin-1.</summary>
    public static byte[] FromText(string text, TextEncodingChoice encoding)
    {
        var bytes = new byte[text.Length];
        var top = encoding == TextEncodingChoice.Latin1 ? 0xFF : 0x7F;
        for (var i = 0; i < text.Length; i++) bytes[i] = text[i] <= top ? (byte)text[i] : (byte)'?';
        return bytes;
    }

    /// <summary>
    /// Reads hex bytes, however they were written: "4D5A9000", "4d 5a 90 00", "0x4D, 0x5A", "\x4D\x5A", or a C
    /// array. With <paramref name="allowWild"/>, ?? is a byte that matches anything. Throws <see cref="FormatException"/>.
    /// </summary>
    public static (byte[] Bytes, bool[] Wild) ParseHex(string text, bool allowWild = false)
    {
        var bytes = new List<byte>();
        var wild = new List<bool>();
        var t = text.Replace("0x", " ", StringComparison.OrdinalIgnoreCase).Replace("\\x", " ", StringComparison.OrdinalIgnoreCase);
        var digits = new StringBuilder();
        void Flush()
        {
            if (digits.Length == 0) return;
            if (digits.Length % 2 == 1) digits.Insert(0, '0'); // "0x5" or a lone "A" means one byte
            for (var i = 0; i < digits.Length; i += 2)
            {
                if (digits[i] == '?' || digits[i + 1] == '?')
                {
                    if (!allowWild || digits[i] != '?' || digits[i + 1] != '?') throw new FormatException("?? stands for a whole byte.");
                    bytes.Add(0);
                    wild.Add(true);
                }
                else
                {
                    bytes.Add(byte.Parse(digits.ToString(i, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                    wild.Add(false);
                }
            }
            digits.Clear();
        }
        foreach (var c in t)
        {
            if (char.IsAsciiHexDigit(c) || c == '?') digits.Append(c);
            else if (char.IsWhiteSpace(c) || c is ',' or '{' or '}' or ';' or '-' or ':') Flush();
            else throw new FormatException($"'{c}' isn't a hex digit.");
        }
        Flush();
        return ([.. bytes], [.. wild]);
    }

    /// <summary>Whether <paramref name="text"/> reads as hex bytes and nothing else.</summary>
    public static bool TryParseHex(string text, out byte[] bytes)
    {
        try
        {
            bytes = ParseHex(text).Bytes;
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    /// <summary>Turns \n, \r, \t, \0, \\ and \xNN in typed text into the characters they stand for.</summary>
    public static string Unescape(string text)
    {
        if (!text.Contains('\\')) return text;
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\\' || i == text.Length - 1) { sb.Append(c); continue; }
            var n = text[++i];
            switch (n)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '0': sb.Append('\0'); break;
                case '\\': sb.Append('\\'); break;
                case 'x' when i + 2 < text.Length && byte.TryParse(text.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var b):
                    sb.Append((char)b);
                    i += 2;
                    break;
                default: sb.Append('\\').Append(n); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Reads an offset typed into the go-to box: "1F40" (hex when <paramref name="hexByDefault"/>, else decimal),
    /// "0x1F40" or "1F40h" (always hex), "#8000" (always decimal), and "+100" or "-100" to move from
    /// <paramref name="current"/>. Returns false when it isn't one.
    /// </summary>
    public static bool TryParseOffset(string text, long current, bool hexByDefault, out long offset)
    {
        offset = 0;
        var t = text.Trim().Replace("_", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        var relative = 0;
        if (t.StartsWith('+')) { relative = 1; t = t[1..]; }
        else if (t.StartsWith('-')) { relative = -1; t = t[1..]; }
        bool hex = hexByDefault;
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { hex = true; t = t[2..]; }
        else if (t.EndsWith('h') || t.EndsWith('H')) { hex = true; t = t[..^1]; }
        else if (t.StartsWith('#')) { hex = false; t = t[1..]; }
        if (t.Length == 0) return false;
        if (!(hex
                ? long.TryParse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v)
                : long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out v)) || v < 0)
            return false;
        offset = relative == 0 ? v : current + relative * v;
        return offset >= 0;
    }

    /// <summary>An offset as shown: "0001F40" in hex (at least eight digits) or "8000" in decimal.</summary>
    public static string Offset(long offset, bool hex, int width = 8) =>
        hex ? offset.ToString("X" + width, CultureInfo.InvariantCulture) : offset.ToString("D" + width, CultureInfo.InvariantCulture);

    /// <summary>A size as people read it: "512 bytes", "12.4 KB", "3.1 GB".</summary>
    public static string Size(long bytes) => bytes switch
    {
        1 => "1 byte",
        < 1024 => $"{bytes:N0} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
