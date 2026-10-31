using Microsoft.Win32.SafeHandles;

namespace AVAHexEdit.Core;

/// <summary>Bytes that can be read from anywhere in them: a document, a snapshot of one, a file or an array.</summary>
public interface IBytes
{
    long Length { get; }

    /// <summary>Copies the bytes from <paramref name="offset"/> into <paramref name="into"/>, and returns how many there were.</summary>
    int Read(long offset, Span<byte> into);
}

/// <summary>Where a document's original bytes come from: a file on disk, or memory for a new one.</summary>
public interface IByteSource : IBytes, IDisposable;

/// <summary>Bytes in memory: a new document, or a test's.</summary>
public sealed class MemorySource(byte[] bytes) : IByteSource
{
    public long Length => bytes.Length;

    public int Read(long offset, Span<byte> into)
    {
        if (offset < 0 || offset >= bytes.Length) return 0;
        var n = (int)Math.Min(into.Length, bytes.Length - offset);
        bytes.AsSpan((int)offset, n).CopyTo(into);
        return n;
    }

    public void Dispose() { }
}

/// <summary>
/// A file read a page at a time as it is looked at, with the last few pages kept, so a file of any size opens at once
/// and only what is shown, searched or saved is ever read. The file stays open for reading only, and others may still
/// write, rename or delete it. Safe to read from more than one thread.
/// </summary>
public sealed class FileSource : IByteSource
{
    public const int PageSize = 64 * 1024;
    private const int KeptPages = 64;

    private sealed record Page(long Index, byte[] Data, int Count);

    private readonly SafeFileHandle _handle;
    private readonly Dictionary<long, LinkedListNode<Page>> _pages = [];
    private readonly LinkedList<Page> _recent = new();
    private readonly Lock _gate = new();

    public string Path { get; }
    public long Length { get; }

    public FileSource(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        _handle = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        Length = RandomAccess.GetLength(_handle);
    }

    public int Read(long offset, Span<byte> into)
    {
        if (offset < 0) return 0;
        lock (_gate)
        {
            var done = 0;
            while (done < into.Length && offset + done < Length)
            {
                var at = offset + done;
                var page = Get(at / PageSize);
                var within = (int)(at % PageSize);
                var n = Math.Min(into.Length - done, page.Count - within);
                if (n <= 0) break; // the file got shorter since it was opened
                page.Data.AsSpan(within, n).CopyTo(into[done..]);
                done += n;
            }
            return done;
        }
    }

    private Page Get(long index)
    {
        if (_pages.TryGetValue(index, out var node))
        {
            _recent.Remove(node);
            _recent.AddFirst(node);
            return node.Value;
        }
        var data = new byte[PageSize];
        var count = 0;
        while (count < PageSize)
        {
            var n = RandomAccess.Read(_handle, data.AsSpan(count), index * PageSize + count);
            if (n == 0) break;
            count += n;
        }
        var page = new Page(index, data, count);
        _pages[index] = _recent.AddFirst(page);
        if (_recent.Count > KeptPages)
        {
            _pages.Remove(_recent.Last!.Value.Index);
            _recent.RemoveLast();
        }
        return page;
    }

    public void Dispose() => _handle.Dispose();
}
