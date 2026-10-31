using System.Security.Cryptography;

namespace AVAHexEdit.Core;

public enum ChecksumKind { Crc32, Adler32, Md5, Sha1, Sha256, Sha512 }

/// <summary>Checksums and hashes of the whole file or a selection, all worked out in one pass over the bytes.</summary>
public static class Checksums
{
    public static string Name(ChecksumKind kind) => kind switch
    {
        ChecksumKind.Crc32 => "CRC-32",
        ChecksumKind.Adler32 => "Adler-32",
        ChecksumKind.Md5 => "MD5",
        ChecksumKind.Sha1 => "SHA-1",
        ChecksumKind.Sha256 => "SHA-256",
        _ => "SHA-512",
    };

    /// <summary>Every kind, in hex (upper case for the checksums, lower case for the hashes, as each is usually written).</summary>
    public static Dictionary<ChecksumKind, string> Compute(IBytes data, ByteRange range, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var crc = 0xFFFFFFFFu;
        uint a = 1, b = 0;
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        var buffer = new byte[1 << 20];
        for (var at = range.Start; at < range.End;)
        {
            cancel.ThrowIfCancellationRequested();
            var n = data.Read(at, buffer.AsSpan(0, (int)Math.Min(buffer.Length, range.End - at)));
            if (n == 0) break;
            var span = buffer.AsSpan(0, n);
            crc = Crc32(crc, span);
            (a, b) = Adler32(a, b, span);
            md5.AppendData(span);
            sha1.AppendData(span);
            sha256.AppendData(span);
            sha512.AppendData(span);
            at += n;
            progress?.Report(range.Length == 0 ? 1 : (double)(at - range.Start) / range.Length);
        }
        return new()
        {
            [ChecksumKind.Crc32] = (crc ^ 0xFFFFFFFFu).ToString("X8"),
            [ChecksumKind.Adler32] = ((b << 16) | a).ToString("X8"),
            [ChecksumKind.Md5] = Convert.ToHexStringLower(md5.GetHashAndReset()),
            [ChecksumKind.Sha1] = Convert.ToHexStringLower(sha1.GetHashAndReset()),
            [ChecksumKind.Sha256] = Convert.ToHexStringLower(sha256.GetHashAndReset()),
            [ChecksumKind.Sha512] = Convert.ToHexStringLower(sha512.GetHashAndReset()),
        };
    }

    private static readonly uint[] CrcTable = MakeTable();

    private static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    /// <summary>CRC-32 as in ZIP, PNG and Ethernet: the running value, before the final inversion.</summary>
    internal static uint Crc32(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var x in bytes) crc = CrcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static (uint, uint) Adler32(uint a, uint b, ReadOnlySpan<byte> bytes)
    {
        const uint mod = 65521;
        // Sums stay under 2^32 for 5552 bytes before they need reducing.
        while (bytes.Length > 0)
        {
            var n = Math.Min(bytes.Length, 5552);
            foreach (var x in bytes[..n])
            {
                a += x;
                b += a;
            }
            a %= mod;
            b %= mod;
            bytes = bytes[n..];
        }
        return (a, b);
    }
}

/// <summary>Two files side by side: where their bytes differ.</summary>
public sealed record Comparison(List<ByteRange> Differences, long DifferentBytes, long LengthA, long LengthB, bool Truncated)
{
    public bool Same => Differences.Count == 0 && LengthA == LengthB;

    /// <summary>"3 differences, 12 bytes; the second file is 4 bytes longer".</summary>
    public string Summary
    {
        get
        {
            if (Same) return "The files are the same.";
            var s = Differences.Count == 0 ? "No bytes differ where both have them"
                : $"{Differences.Count:N0}{(Truncated ? "+" : "")} difference{(Differences.Count == 1 ? "" : "s")}, {ByteFormats.Size(DifferentBytes)}";
            if (LengthA != LengthB)
                s += $"; the {(LengthB > LengthA ? "second" : "first")} file is {ByteFormats.Size(Math.Abs(LengthB - LengthA))} longer";
            return s + ".";
        }
    }

    /// <summary>
    /// Compares byte for byte at the same offsets (as hex editors do; an inserted byte makes the rest differ). Bytes
    /// past the end of the shorter file count as one difference. At most <paramref name="limit"/> runs are listed.
    /// </summary>
    public static Comparison Run(IBytes a, IBytes b, int limit = 100_000, CancellationToken cancel = default)
    {
        const int block = 1 << 20;
        var both = Math.Min(a.Length, b.Length);
        var runs = new List<ByteRange>();
        long differing = 0;
        long runStart = -1;
        var truncated = false;
        var ba = new byte[block];
        var bb = new byte[block];
        for (long at = 0; at < both && !truncated; at += block)
        {
            cancel.ThrowIfCancellationRequested();
            var n = (int)Math.Min(block, both - at);
            a.Read(at, ba.AsSpan(0, n));
            b.Read(at, bb.AsSpan(0, n));
            var sa = ba.AsSpan(0, n);
            var sb = bb.AsSpan(0, n);
            var i = 0;
            while (i < n)
            {
                if (runStart < 0)
                {
                    var same = sa[i..].CommonPrefixLength(sb[i..]);
                    i += same;
                    if (i >= n) break;
                    runStart = at + i;
                }
                while (i < n && sa[i] != sb[i]) i++;
                if (i < n)
                {
                    runs.Add(new ByteRange(runStart, at + i - runStart));
                    differing += at + i - runStart;
                    runStart = -1;
                    if (runs.Count >= limit) { truncated = true; break; }
                }
            }
        }
        if (runStart >= 0 && !truncated)
        {
            runs.Add(new ByteRange(runStart, both - runStart));
            differing += both - runStart;
        }
        if (a.Length != b.Length && !truncated)
        {
            var extra = Math.Abs(a.Length - b.Length);
            // A difference that runs right up to the end of the shorter file joins the extra bytes.
            if (runs.Count > 0 && runs[^1].End == both) runs[^1] = runs[^1] with { Length = runs[^1].Length + extra };
            else runs.Add(new ByteRange(both, extra));
            differing += extra;
        }
        return new Comparison(runs, differing, a.Length, b.Length, truncated);
    }
}
