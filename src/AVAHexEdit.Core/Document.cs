namespace AVAHexEdit.Core;

/// <summary>A run of bytes, by where it starts and how long it is.</summary>
public readonly record struct ByteRange(long Start, long Length)
{
    public long End => Start + Length;
    public bool Contains(long offset) => offset >= Start && offset < End;
}

/// <summary>
/// A file being edited. The original bytes are never copied or changed: the document is a list of pieces, each a run
/// of either the original or of a buffer that only ever grows with what was typed or pasted (a piece table). So a file
/// of any size opens at once, inserting and deleting cost the same anywhere in it, and every state the document has
/// been in is just a list of pieces, which is what undo keeps. Edit it from one thread; <see cref="Snapshot"/> gives
/// a copy that other threads can read while it changes.
/// </summary>
public sealed class Document : IBytes, IDisposable
{
    private readonly record struct Piece(bool Added, long Start, long Length);

    /// <summary>One state of the document: its pieces, where each one starts, and an id that tells states apart.</summary>
    private sealed class State
    {
        public readonly Piece[] Pieces;
        public readonly long[] Starts;
        public readonly long Length;
        public readonly long Id;
        private static long _ids;

        public State(Piece[] pieces)
        {
            Pieces = pieces;
            Starts = new long[pieces.Length];
            long at = 0;
            for (var i = 0; i < pieces.Length; i++)
            {
                Starts[i] = at;
                at += pieces[i].Length;
            }
            Length = at;
            Id = Interlocked.Increment(ref _ids);
        }

        /// <summary>The piece <paramref name="offset"/> is in.</summary>
        public int Find(long offset)
        {
            var i = Array.BinarySearch(Starts, offset);
            if (i < 0) i = ~i - 1;
            // Skip empty pieces (there are none, but be safe) so the piece found really holds the offset.
            while (i < Pieces.Length - 1 && Starts[i] + Pieces[i].Length <= offset) i++;
            return Math.Max(i, 0);
        }
    }

    private readonly record struct Step(State Before, long Where);

    private IByteSource _source;
    private byte[] _added = new byte[4096];
    private int _addedLength;
    private State _state;
    private readonly Stack<Step> _undo = new();
    private readonly Stack<Step> _redo = new();
    private long _savedId;
    private bool _canMerge;

    /// <summary>The file this was opened from or last saved to, or null for a new one never saved.</summary>
    public string? Path { get; private set; }

    public long Length => _state.Length;
    public bool IsModified => _state.Id != _savedId;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised after every change, undo and redo included, and after saving.</summary>
    public event Action? Changed;

    private Document(IByteSource source, string? path)
    {
        _source = source;
        Path = path;
        _state = Whole(source);
        _savedId = _state.Id;
    }

    private static State Whole(IByteSource source) =>
        new(source.Length > 0 ? [new Piece(false, 0, source.Length)] : []);

    /// <summary>An empty document, not yet saved anywhere.</summary>
    public static Document New() => new(new MemorySource([]), null);

    /// <summary>A document of these bytes, not yet saved anywhere.</summary>
    public static Document FromBytes(byte[] bytes) => new(new MemorySource(bytes), null);

    /// <summary>
    /// A read-only look at some bytes (such as a <see cref="Snapshot"/>), named <paramref name="name"/>: for showing
    /// them, as the compare window does. The bytes stay with their owner.
    /// </summary>
    public static Document View(IBytes bytes, string name) => new(new Borrowed(bytes), null) { _name = name };

    private sealed class Borrowed(IBytes bytes) : IByteSource
    {
        public long Length => bytes.Length;
        public int Read(long offset, Span<byte> into) => bytes.Read(offset, into);
        public void Dispose() { }
    }

    private string? _name;

    /// <summary>Opens a file. Nothing is read until it is looked at.</summary>
    public static Document Open(string path)
    {
        var source = new FileSource(path);
        return new Document(source, source.Path);
    }

    /// <summary>The name to show: the file's name, or "Untitled".</summary>
    public string Name => _name ?? (Path is null ? "Untitled" : System.IO.Path.GetFileName(Path));

    // Reading

    public int Read(long offset, Span<byte> into) => Read(_state, _added, _source, offset, into, default);

    /// <summary>Reads, and marks in <paramref name="changed"/> each byte that was typed or pasted rather than in the file.</summary>
    public int Read(long offset, Span<byte> into, Span<bool> changed) => Read(_state, _added, _source, offset, into, changed);

    public byte this[long offset]
    {
        get
        {
            Span<byte> one = stackalloc byte[1];
            if (Read(offset, one) != 1) throw new ArgumentOutOfRangeException(nameof(offset));
            return one[0];
        }
    }

    /// <summary>The bytes from <paramref name="offset"/>, as an array (as many as there are, up to <paramref name="count"/>).</summary>
    public byte[] ReadArray(long offset, int count)
    {
        var bytes = new byte[(int)Math.Clamp(Length - offset, 0, count)];
        Read(offset, bytes);
        return bytes;
    }

    private static int Read(State state, byte[] added, IByteSource source, long offset, Span<byte> into, Span<bool> changed)
    {
        if (offset < 0 || offset >= state.Length || into.IsEmpty) return 0;
        var want = (int)Math.Min(into.Length, state.Length - offset);
        var done = 0;
        var i = state.Find(offset);
        while (done < want && i < state.Pieces.Length)
        {
            var p = state.Pieces[i];
            var within = offset + done - state.Starts[i];
            var n = (int)Math.Min(want - done, p.Length - within);
            var to = into.Slice(done, n);
            if (p.Added)
            {
                added.AsSpan((int)(p.Start + within), n).CopyTo(to);
            }
            else
            {
                var got = source.Read(p.Start + within, to);
                if (got < n) to[got..].Clear(); // the file got shorter under us: show zeros rather than fail
            }
            if (!changed.IsEmpty) changed.Slice(done, n).Fill(p.Added);
            done += n;
            i++;
        }
        return done;
    }

    /// <summary>
    /// A copy of the document as it is now, which stays the same while the document changes and can be read from any
    /// thread: for searching, checksums and templates in the background.
    /// </summary>
    public IBytes Snapshot() => new Frozen(_state, _added, _source);

    private sealed class Frozen(State state, byte[] added, IByteSource source) : IBytes
    {
        public long Length => state.Length;
        public int Read(long offset, Span<byte> into) => Document.Read(state, added, source, offset, into, default);
    }

    // Editing

    public void Insert(long at, ReadOnlySpan<byte> bytes, bool merge = false) => Replace(at, 0, bytes, merge);

    public void Delete(long at, long count, bool merge = false) => Replace(at, count, [], merge);

    /// <summary>Writes over the bytes from <paramref name="at"/>, making the document longer if it runs past the end.</summary>
    public void Overwrite(long at, ReadOnlySpan<byte> bytes, bool merge = false) =>
        Replace(at, Math.Min(bytes.Length, Length - at), bytes, merge);

    /// <summary>
    /// Takes out <paramref name="remove"/> bytes at <paramref name="at"/> and puts <paramref name="insert"/> in their
    /// place, as one undo step. With <paramref name="merge"/>, it joins the step before (typing a run of bytes undoes
    /// as one), unless something was undone, redone or saved since.
    /// </summary>
    public void Replace(long at, long remove, ReadOnlySpan<byte> insert, bool merge = false)
    {
        if (at < 0 || at > Length) throw new ArgumentOutOfRangeException(nameof(at));
        remove = Math.Clamp(remove, 0, Length - at);
        if (remove == 0 && insert.IsEmpty) return;
        var pieces = new List<Piece>(_state.Pieces.Length + 3);
        Slice(_state, 0, at, pieces);
        if (!insert.IsEmpty) Add(pieces, new Piece(true, Append(insert), insert.Length));
        Slice(_state, at + remove, Length, pieces);
        Commit(new State([.. pieces]), at, merge);
    }

    /// <summary>
    /// Replaces every run of <paramref name="length"/> bytes starting at each of <paramref name="starts"/> (in order,
    /// not overlapping) with <paramref name="with"/>, as one undo step: replace all.
    /// </summary>
    public void ReplaceRuns(IReadOnlyList<long> starts, long length, ReadOnlySpan<byte> with)
    {
        if (starts.Count == 0) return;
        var withStart = with.IsEmpty ? 0 : Append(with);
        var pieces = new List<Piece>(_state.Pieces.Length + starts.Count * 2);
        long at = 0;
        foreach (var s in starts)
        {
            if (s < at || s + length > Length) throw new ArgumentException("The runs must be in order, not overlap, and lie in the document.", nameof(starts));
            Slice(_state, at, s, pieces);
            if (!with.IsEmpty) Add(pieces, new Piece(true, withStart, with.Length));
            at = s + length;
        }
        Slice(_state, at, Length, pieces);
        Commit(new State([.. pieces]), starts[0], false);
    }

    private void Commit(State next, long where, bool merge)
    {
        if (!(merge && _canMerge && _undo.Count > 0)) _undo.Push(new Step(_state, where));
        _redo.Clear();
        _state = next;
        _canMerge = true;
        Changed?.Invoke();
    }

    /// <summary>Appends the pieces of <paramref name="state"/> that cover [from, to).</summary>
    private static void Slice(State state, long from, long to, List<Piece> into)
    {
        if (to <= from) return;
        var i = state.Find(from);
        while (i < state.Pieces.Length && state.Starts[i] < to)
        {
            var p = state.Pieces[i];
            var s = Math.Max(from, state.Starts[i]);
            var e = Math.Min(to, state.Starts[i] + p.Length);
            if (e > s) Add(into, new Piece(p.Added, p.Start + (s - state.Starts[i]), e - s));
            i++;
        }
    }

    /// <summary>Adds a piece, joined to the one before when it carries straight on from it.</summary>
    private static void Add(List<Piece> pieces, Piece p)
    {
        if (p.Length <= 0) return;
        if (pieces.Count > 0 && pieces[^1] is var last && last.Added == p.Added && last.Start + last.Length == p.Start)
            pieces[^1] = last with { Length = last.Length + p.Length };
        else
            pieces.Add(p);
    }

    private long Append(ReadOnlySpan<byte> bytes)
    {
        if (_addedLength + (long)bytes.Length > Array.MaxLength) throw new InvalidOperationException("That's more than can be held in one editing session; save and reopen first.");
        if (_addedLength + bytes.Length > _added.Length)
        {
            // A new, bigger array: snapshots keep the old one, which never changes where they read.
            var bigger = new byte[(int)Math.Min(Array.MaxLength, Math.Max(_added.Length * 2L, _addedLength + (long)bytes.Length))];
            _added.AsSpan(0, _addedLength).CopyTo(bigger);
            _added = bigger;
        }
        bytes.CopyTo(_added.AsSpan(_addedLength));
        var start = _addedLength;
        _addedLength += bytes.Length;
        return start;
    }

    /// <summary>Goes back a step, and returns where the change was (to put the caret), or null if there is nothing to undo.</summary>
    public long? Undo()
    {
        if (!_undo.TryPop(out var step)) return null;
        _redo.Push(new Step(_state, step.Where));
        _state = step.Before;
        _canMerge = false;
        Changed?.Invoke();
        return step.Where;
    }

    public long? Redo()
    {
        if (!_redo.TryPop(out var step)) return null;
        _undo.Push(new Step(_state, step.Where));
        _state = step.Before;
        _canMerge = false;
        Changed?.Invoke();
        return step.Where;
    }

    /// <summary>Stops the next edit joining the last one, so it undoes on its own.</summary>
    public void EndMerge() => _canMerge = false;

    /// <summary>The runs of bytes that differ from the file as opened (typed, pasted or moved by an insert).</summary>
    public IEnumerable<ByteRange> ChangedRuns()
    {
        var s = _state;
        for (var i = 0; i < s.Pieces.Length; i++)
            if (s.Pieces[i].Added) yield return new ByteRange(s.Starts[i], s.Pieces[i].Length);
    }

    // Saving

    /// <summary>
    /// Saves to <paramref name="path"/> (the file it came from, or another): written to a new file beside it first,
    /// which then takes the old one's place, so a failure part way leaves the old file as it was. Afterwards the
    /// document reads from the saved file, and the undo history starts again.
    /// </summary>
    public void Save(string path, IProgress<double>? progress = null, CancellationToken cancel = default) =>
        FinishSave(WriteCopy(path, progress, cancel), path);

    /// <summary>
    /// The slow half of saving, safe to run on another thread: writes the document as it is now to a new file beside
    /// <paramref name="path"/>, and returns that file's name for <see cref="FinishSave"/>.
    /// </summary>
    public string WriteCopy(string path, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var full = System.IO.Path.GetFullPath(path);
        var dir = System.IO.Path.GetDirectoryName(full) ?? ".";
        var temp = System.IO.Path.Combine(dir, $".{System.IO.Path.GetFileName(full)}.{Guid.NewGuid():N}.saving");
        var snapshot = Snapshot();
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buffer = new byte[1 << 20];
                for (long at = 0; at < snapshot.Length;)
                {
                    cancel.ThrowIfCancellationRequested();
                    var n = snapshot.Read(at, buffer);
                    if (n == 0) break;
                    output.Write(buffer, 0, n);
                    at += n;
                    progress?.Report((double)at / snapshot.Length);
                }
                output.Flush(true);
            }
            if (!OperatingSystem.IsWindows() && File.Exists(full))
                File.SetUnixFileMode(temp, File.GetUnixFileMode(full));
            return temp;
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>The quick half of saving, on the document's own thread: puts the copy in place and reads from it.</summary>
    public void FinishSave(string written, string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var old = _source;
        // Windows won't replace a file that is open, so let go of it first; if the move fails, open it again.
        var reopen = old is FileSource f && string.Equals(f.Path, full, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (reopen) old.Dispose();
        try
        {
            File.Move(written, full, overwrite: true);
        }
        catch
        {
            if (reopen) _source = new FileSource(full);
            try { File.Delete(written); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        if (!reopen) old.Dispose();
        _source = new FileSource(full);
        Path = full;
        _added = new byte[4096];
        _addedLength = 0;
        _state = Whole(_source);
        _savedId = _state.Id;
        _undo.Clear();
        _redo.Clear();
        _canMerge = false;
        Changed?.Invoke();
    }

    public void Dispose() => _source.Dispose();
}
