using AVAHexEdit.Core;

namespace AVAHexEdit.Tests.Core;

public class DocumentTests
{
    private static byte[] All(IBytes d)
    {
        var b = new byte[d.Length];
        Assert.Equal(b.Length, d.Read(0, b));
        return b;
    }

    private static string TempFile(byte[] bytes)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("avahexedit-").FullName, "file.bin");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Insert_delete_and_overwrite_anywhere_and_undo_and_redo_each()
    {
        using var d = Document.FromBytes([1, 2, 3, 4, 5]);
        d.Insert(2, [9, 9]);
        Assert.Equal([1, 2, 9, 9, 3, 4, 5], All(d));
        d.Delete(0, 1);
        Assert.Equal([2, 9, 9, 3, 4, 5], All(d));
        d.Overwrite(4, [7, 7, 7]); // runs past the end: the document grows
        Assert.Equal([2, 9, 9, 3, 7, 7, 7], All(d));
        Assert.True(d.IsModified);

        Assert.Equal(4, d.Undo());
        Assert.Equal([2, 9, 9, 3, 4, 5], All(d));
        Assert.Equal(0, d.Undo());
        Assert.Equal(2, d.Undo());
        Assert.Equal([1, 2, 3, 4, 5], All(d));
        Assert.False(d.IsModified);
        Assert.Null(d.Undo());

        d.Redo();
        d.Redo();
        Assert.Equal([2, 9, 9, 3, 4, 5], All(d));
        Assert.True(d.CanRedo);
        d.Insert(0, [0]); // a new edit clears what could be redone
        Assert.False(d.CanRedo);
    }

    [Fact]
    public void Typing_a_run_of_bytes_undoes_as_one_step_but_not_after_an_undo()
    {
        using var d = Document.FromBytes([0, 0, 0, 0]);
        d.Overwrite(0, [1]);
        d.Overwrite(1, [2], merge: true);
        d.Overwrite(2, [3], merge: true);
        Assert.Equal([1, 2, 3, 0], All(d));
        d.Undo();
        Assert.Equal([0, 0, 0, 0], All(d));
        d.Redo();
        d.Overwrite(3, [4], merge: true); // after a redo it doesn't join
        d.Undo();
        Assert.Equal([1, 2, 3, 0], All(d));
    }

    [Fact]
    public void Changed_bytes_are_marked_and_a_snapshot_stays_as_it_was()
    {
        using var d = Document.FromBytes([1, 2, 3, 4]);
        var before = d.Snapshot();
        d.Overwrite(1, [8, 8]);
        var bytes = new byte[4];
        var changed = new bool[4];
        d.Read(0, bytes, changed);
        Assert.Equal([false, true, true, false], changed);
        Assert.Equal([new ByteRange(1, 2)], d.ChangedRuns());
        Assert.Equal([1, 2, 3, 4], All(before));
    }

    [Fact]
    public void Replace_all_is_one_step()
    {
        using var d = Document.FromBytes("a-b-c-d"u8.ToArray());
        d.ReplaceRuns([1, 3, 5], 1, "::"u8);
        Assert.Equal("a::b::c::d"u8.ToArray(), All(d));
        d.Undo();
        Assert.Equal("a-b-c-d"u8.ToArray(), All(d));
    }

    [Fact]
    public void Many_scattered_edits_read_back_the_same_as_an_array_given_the_same_edits()
    {
        var rng = new Random(7);
        var reference = Enumerable.Range(0, 5000).Select(i => (byte)i).ToList();
        using var d = Document.FromBytes([.. reference]);
        for (var i = 0; i < 2000; i++)
        {
            var at = rng.Next(reference.Count + 1);
            switch (rng.Next(3))
            {
                case 0:
                    var ins = new byte[rng.Next(1, 5)];
                    rng.NextBytes(ins);
                    d.Insert(at, ins);
                    reference.InsertRange(at, ins);
                    break;
                case 1:
                    var n = Math.Min(rng.Next(1, 6), reference.Count - at);
                    d.Delete(at, n);
                    reference.RemoveRange(at, n);
                    break;
                default:
                    var over = new byte[rng.Next(1, 4)];
                    rng.NextBytes(over);
                    d.Overwrite(at, over);
                    for (var k = 0; k < over.Length; k++)
                        if (at + k < reference.Count) reference[at + k] = over[k]; else reference.Add(over[k]);
                    break;
            }
        }
        Assert.Equal(reference.ToArray(), All(d));
        // Reading from the middle, across pieces, gives the same bytes too.
        var part = new byte[300];
        d.Read(1234, part);
        Assert.Equal(reference.Skip(1234).Take(300).ToArray(), part);
    }

    [Fact]
    public void Saving_over_the_open_file_writes_the_edits_and_starts_afresh()
    {
        var bytes = new byte[300_000]; // several pages of the file
        new Random(1).NextBytes(bytes);
        var path = TempFile(bytes);
        using var d = Document.Open(path);
        Assert.Equal(bytes.Length, d.Length);
        Assert.Equal(bytes[200_000], d[200_000]);
        d.Insert(0, [0xAA, 0xBB]);
        d.Delete(150_000, 10);
        d.Overwrite(d.Length - 1, [0xCC]);
        var expected = All(d);
        Assert.True(d.IsModified);

        d.Save(path);

        Assert.False(d.IsModified);
        Assert.False(d.CanUndo);
        Assert.Equal(expected, File.ReadAllBytes(path));
        Assert.Equal(expected, All(d));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.saving"));
        // It still edits after saving.
        d.Overwrite(0, [1]);
        Assert.Equal(1, d[0]);
    }

    [Fact]
    public void Save_as_writes_a_new_file_and_leaves_the_old_one()
    {
        var path = TempFile([1, 2, 3]);
        var other = Path.Combine(Path.GetDirectoryName(path)!, "other.bin");
        using var d = Document.Open(path);
        d.Insert(3, [4]);
        d.Save(other);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(other));
        Assert.Equal(Path.GetFullPath(other), d.Path);
        Assert.Equal("other.bin", d.Name);
    }

    [Fact]
    public void A_new_document_can_be_typed_into_and_saved()
    {
        using var d = Document.New();
        Assert.Equal(0, d.Length);
        Assert.Equal("Untitled", d.Name);
        d.Insert(0, "hello"u8);
        var path = Path.Combine(Directory.CreateTempSubdirectory("avahexedit-").FullName, "new.txt");
        d.Save(path);
        Assert.Equal("hello", File.ReadAllText(path));
    }

    [Fact]
    public void A_file_bigger_than_four_gigabytes_opens_at_once_and_edits_anywhere()
    {
        // A sparse file: five gigabytes long, taking no room on the disk, with one byte set near the end.
        var path = Path.Combine(Directory.CreateTempSubdirectory("avahexedit-").FullName, "huge.bin");
        const long size = 5L * 1024 * 1024 * 1024;
        using (var f = new FileStream(path, FileMode.CreateNew))
        {
            f.SetLength(size);
            f.Position = size - 10;
            f.WriteByte(0x5A);
        }
        try
        {
            using var d = Document.Open(path);
            Assert.Equal(size, d.Length);
            Assert.Equal(0x5A, d[size - 10]);
            d.Insert(0, [1, 2, 3]);
            d.Delete(size / 2, 1);
            Assert.Equal(size + 2, d.Length);
            Assert.Equal(0x5A, d[size - 8]);
            Assert.Equal(1, d[0]);
            var tail = new byte[16];
            Assert.Equal(10, d.Read(size + 2 - 10, tail));
            Assert.Equal(size - 8, Finder.Find(d, new Pattern([0x5A]), size - 100));
            Assert.Equal(size - 8, Finder.Find(d, new Pattern([0x5A]), d.Length, forward: false));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
