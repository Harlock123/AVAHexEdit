using System.IO.Compression;
using System.Text;
using AVAHexEdit.Core;

namespace AVAHexEdit.Tests.Core;

public class TemplateTests
{
    private static readonly TemplateLibrary Library = TemplateLibrary.Load(null);

    private static TemplateResult Run(string templateName, byte[] bytes)
    {
        var t = Library.Templates.Single(x => x.Name == templateName);
        return TemplateRunner.Run(t, Document.FromBytes(bytes));
    }

    private static TemplateResult RunText(string template, byte[] bytes) =>
        TemplateRunner.Run(Template.Parse(template), Document.FromBytes(bytes));

    private static TemplateNode Node(TemplateNode root, string path)
    {
        var n = root;
        foreach (var part in path.Split('/'))
            n = n.Children.FirstOrDefault(c => c.Name == part) ?? throw new Xunit.Sdk.XunitException($"No {part} in {n.Name}: {string.Join(", ", n.Children.Select(c => c.Name))}");
        return n;
    }

    // Sample files

    private static byte[] Chunk(string type, byte[] data)
    {
        var b = new List<byte>();
        b.AddRange(BigEndian((uint)data.Length));
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        b.AddRange(typed);
        b.AddRange(BigEndian(Checksums.Crc32(0xFFFFFFFF, typed) ^ 0xFFFFFFFF));
        return [.. b];
    }

    private static byte[] BigEndian(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    internal static byte[] Png() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        .. Chunk("IHDR", [.. BigEndian(640), .. BigEndian(480), 8, 6, 0, 0, 0]),
        .. Chunk("tEXt", [.. "Software"u8, 0, .. "AVAHexEdit"u8]),
        .. Chunk("IDAT", [1, 2, 3, 4]),
        .. Chunk("IEND", []),
    ];

    [Fact]
    public void Every_built_in_template_reads_and_none_has_a_mistake()
    {
        Assert.Empty(Library.Problems);
        Assert.Equal(
            ["BMP bitmap", "ELF executable", "GIF image", "LightWave object (LWO2)", "LightWave object (LWOB)", "PNG image", "WAV audio", "Windows executable (PE)", "ZIP archive"],
            Library.Templates.Select(t => t.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_png_shows_each_chunk_by_type_with_its_header_and_text_read()
    {
        var r = Run("PNG image", Png());
        Assert.Null(r.Error);
        var chunks = Node(r.Root, "chunk");
        Assert.Equal(["IHDR", "tEXt", "IDAT", "IEND"], chunks.Children.Select(c => c.Value));
        var ihdr = chunks.Children[0];
        Assert.Equal("640 (0x280)", Node(ihdr, "header/width").Value);
        Assert.Equal("6 (truecolour and alpha)", Node(ihdr, "header/colourType").Value);
        Assert.Equal("\"Software\"", Node(chunks.Children[1], "text/keyword").Value);
        Assert.Equal("\"AVAHexEdit\"", Node(chunks.Children[1], "text/text").Value);
        Assert.Equal(8, ihdr.Offset);
        Assert.Equal(25, ihdr.Length);
        Assert.Equal("chunk › chunk[0] › header › width", Node(ihdr, "header/width").Path);
        // The fields cover the file from end to end, in order, with no gaps.
        long at = 0;
        foreach (var f in r.Fields)
        {
            Assert.Equal(at, f.Offset);
            at = f.End;
        }
        Assert.Equal(Png().Length, at);
        Assert.Equal("width", r.FieldAt(16)!.Name);
        Assert.Equal("crc", r.FieldAt(29)!.Name);
        Assert.Null(r.FieldAt(5000));
    }

    [Fact]
    public void A_wav_reads_its_format_and_pads_odd_chunks()
    {
        var fmt = new List<byte>();
        fmt.AddRange(BitConverter.GetBytes((ushort)1));
        fmt.AddRange(BitConverter.GetBytes((ushort)2));
        fmt.AddRange(BitConverter.GetBytes(44100));
        fmt.AddRange(BitConverter.GetBytes(44100 * 4));
        fmt.AddRange(BitConverter.GetBytes((ushort)4));
        fmt.AddRange(BitConverter.GetBytes((ushort)16));
        byte[] info = [.. "INFO"u8, .. "INAM"u8, .. BitConverter.GetBytes(5), .. "Song\0"u8, 0];
        var body = new List<byte>();
        body.AddRange("WAVE"u8.ToArray());
        body.AddRange("fmt "u8.ToArray()); body.AddRange(BitConverter.GetBytes(fmt.Count)); body.AddRange(fmt);
        body.AddRange("LIST"u8.ToArray()); body.AddRange(BitConverter.GetBytes(info.Length)); body.AddRange(info);
        body.AddRange("data"u8.ToArray()); body.AddRange(BitConverter.GetBytes(3)); body.AddRange(new byte[] { 1, 2, 3, 0 });
        byte[] wav = [.. "RIFF"u8, .. BitConverter.GetBytes(body.Count), .. body];

        var r = Run("WAV audio", wav);
        Assert.Null(r.Error);
        var chunks = Node(r.Root, "chunk").Children;
        Assert.Equal(["fmt ", "LIST", "data"], chunks.Select(c => c.Value));
        Assert.Equal("1 (PCM)", Node(chunks[0], "format/audioFormat").Value);
        Assert.Equal("44100 (0xAC44)", Node(chunks[0], "format/sampleRate").Value);
        Assert.Equal("\"Song\"", Node(chunks[1], "list/item/item[0]/text").Value);
        Assert.Equal("(padding)", chunks[2].Children[^1].Name);
        Assert.Equal(wav.Length, r.Fields[^1].End);
    }

    [Fact]
    public void A_bmp_reads_its_headers_and_finds_the_pixels()
    {
        var bmp = new List<byte>();
        bmp.AddRange("BM"u8.ToArray());
        bmp.AddRange(BitConverter.GetBytes(54 + 16));
        bmp.AddRange(new byte[4]);
        bmp.AddRange(BitConverter.GetBytes(54));
        bmp.AddRange(BitConverter.GetBytes(40));
        bmp.AddRange(BitConverter.GetBytes(2));
        bmp.AddRange(BitConverter.GetBytes(-2));
        bmp.AddRange(BitConverter.GetBytes((ushort)1));
        bmp.AddRange(BitConverter.GetBytes((ushort)24));
        bmp.AddRange(new byte[24]);
        bmp.AddRange(Enumerable.Repeat((byte)0x7F, 16));
        var r = Run("BMP bitmap", [.. bmp]);
        Assert.Null(r.Error);
        Assert.Equal("-2", Node(r.Root, "infoHeader/height").Value);
        Assert.Equal("0 (none (BI_RGB))", Node(r.Root, "infoHeader/compression").Value);
        var pixels = Node(r.Root, "pixels");
        Assert.Equal(54, pixels.Offset);
        Assert.Equal(16, pixels.Length);
    }

    [Fact]
    public void A_zip_lists_each_file_and_the_central_directory()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("readme.txt", CompressionLevel.NoCompression).Open())) w.Write("Hello");
            using (var w = new StreamWriter(zip.CreateEntry("data/numbers.txt", CompressionLevel.Optimal).Open())) w.Write(string.Concat(Enumerable.Repeat("0123456789", 50)));
        }
        var r = Run("ZIP archive", ms.ToArray());
        Assert.Null(r.Error);
        Assert.Equal(["readme.txt", "data/numbers.txt"], Node(r.Root, "file").Children.Select(c => c.Value));
        Assert.Equal("0 (stored)", Node(Node(r.Root, "file").Children[0], "method").Value);
        Assert.Equal("8 (deflate)", Node(Node(r.Root, "file").Children[1], "method").Value);
        Assert.Equal(["readme.txt", "data/numbers.txt"], Node(r.Root, "entry").Children.Select(c => c.Value));
        Assert.Equal("2", Node(r.Root, "end/entries").Value);
        Assert.Equal(ms.Length, r.Fields[^1].End);
    }

    [Fact]
    public void A_gif_reads_its_screen_palette_and_blocks()
    {
        byte[] gif =
        [
            0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00,
            0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00,
            0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x02, 0x44, 0x01, 0x00, 0x3B,
        ];
        var r = Run("GIF image", gif);
        Assert.Null(r.Error);
        Assert.Equal(2, Node(r.Root, "colour").Children.Count);
        Assert.Equal(["extension", "image"], Node(r.Root, "block").Children.Select(c => c.Value));
        Assert.Equal("249 (graphic control)", Node(Node(r.Root, "block").Children[0], "extensionType").Value);
        Assert.Equal(gif.Length, r.Fields[^1].End);
    }

    [Fact]
    public void A_lightwave_lwob_object_reads_points_surfaces_and_polygons()
    {
        var pnts = new List<byte>();
        foreach (var f in new[] { 0f, 1f, 2f, 3f, 4f, 5f }) pnts.AddRange(BigEndian(BitConverter.SingleToUInt32Bits(f)));
        byte[] srfs = [.. "Default"u8, 0];
        byte[] pols = [0, 2, 0, 0, 0, 1, 0, 1];
        var body = new List<byte>("LWOB"u8.ToArray());
        void Add(string id, byte[] data) { body.AddRange(Encoding.ASCII.GetBytes(id)); body.AddRange(BigEndian((uint)data.Length)); body.AddRange(data); }
        Add("PNTS", [.. pnts]);
        Add("SRFS", srfs);
        Add("POLS", pols);
        byte[] lwo = [.. "FORM"u8, .. BigEndian((uint)body.Count), .. body];

        var lib = Library.Pick(Document.FromBytes(lwo), "ship.lwo");
        Assert.Equal("LightWave object (LWOB)", lib?.Name);
        var r = TemplateRunner.Run(lib!, Document.FromBytes(lwo));
        Assert.Null(r.Error);
        var chunks = Node(r.Root, "chunk").Children;
        Assert.Equal(["PNTS", "SRFS", "POLS"], chunks.Select(c => c.Value));
        Assert.Equal("5", Node(chunks[0], "point/point[1]/z").Value);
        Assert.Equal("\"Default\"", Node(chunks[1], "names/name/name[0]/text").Value);
        Assert.Equal("0, 1", Node(chunks[2], "polygons/polygon/polygon[0]/points").Value);
    }

    [Fact]
    public void Windows_executables_and_elf_files_show_their_headers()
    {
        // This test assembly is itself a PE file.
        var dll = File.ReadAllBytes(typeof(TemplateTests).Assembly.Location);
        var pick = Library.Pick(Document.FromBytes(dll), "x.dll");
        Assert.Equal("Windows executable (PE)", pick?.Name);
        var r = TemplateRunner.Run(pick!, Document.FromBytes(dll));
        Assert.Null(r.Error);
        Assert.Equal("\"PE\"", Node(r.Root, "peHeader/signature").Value);
        Assert.Contains(".text", Node(r.Root, "section").Children.Select(c => c.Value));

        var elf = new byte[64];
        byte[] ident = [0x7F, 0x45, 0x4C, 0x46, 2, 1, 1, 3];
        ident.CopyTo(elf, 0);
        BitConverter.GetBytes((ushort)3).CopyTo(elf, 16);
        BitConverter.GetBytes((ushort)183).CopyTo(elf, 18);
        BitConverter.GetBytes((ushort)64).CopyTo(elf, 52);
        var e = Run("ELF executable", elf);
        Assert.Null(e.Error);
        Assert.Equal("183 (AArch64)", Node(e.Root, "header/machine").Value);
        Assert.Equal("3 (shared object)", Node(e.Root, "header/type").Value);
        Assert.Equal("2 (64-bit)", Node(e.Root, "ident/class").Value);
    }

    [Fact]
    public void The_language_has_arrays_sized_structs_seek_align_and_expressions()
    {
        var r = RunText("""
            name "Test"
            endian big
            u16 count
            u8[count] values
            struct box[4] {
              u8 a
            }
            align 4
            seek 8
            u16 last { 0x0102: "one two" }
            if last == 0x0102 && count * 2 == 6 { u8 yes } else { u8 no }
            repeat item while u8at(pos) != 0xFF label v { u8 v }
            u8 end
            """, [0, 3, 10, 20, 30, 7, 8, 9, 1, 2, 5, 1, 2, 0xFF]);
        Assert.Null(r.Error);
        Assert.Equal("10, 20, 30", Node(r.Root, "values").Value);
        Assert.Equal(3, Node(r.Root, "values").Children.Count);
        Assert.Equal("(rest)", Node(r.Root, "box").Children[^1].Name);
        Assert.Equal(5, Node(r.Root, "box").Offset);
        Assert.Equal(4, Node(r.Root, "box").Length);
        Assert.Equal("258 (one two)", Node(r.Root, "last").Value);
        Assert.Equal("5", Node(r.Root, "yes").Value);
        Assert.Equal(["1", "2"], Node(r.Root, "item").Children.Select(c => c.Value));
        Assert.Equal("255 (0xFF)", Node(r.Root, "end").Value);
    }

    [Fact]
    public void A_field_past_the_end_stops_it_and_says_where_keeping_what_was_read()
    {
        var r = RunText("u32 size\nbytes[size] data\nu8 after", [10, 0, 0, 0, 1, 2]);
        Assert.Equal("line 2: data needs 10 bytes at 0x4, but the end of the file is 2 bytes away.", r.Error);
        Assert.Equal(["size"], r.Root.Children.Select(c => c.Name));
        Assert.Equal("line 1: there's no field called nope before this.", RunText("bytes[nope] x", [1]).Error);
    }

    [Fact]
    public void Mistakes_in_a_template_are_found_with_their_line()
    {
        Assert.Equal(2, Assert.Throws<TemplateException>(() => Template.Parse("u8 a\nu33 b")).Line);
        Assert.Contains("isn't a type", Assert.Throws<TemplateException>(() => Template.Parse("u8 a\nu33 b")).Message);
        Assert.Contains("missing its closing '}'", Assert.Throws<TemplateException>(() => Template.Parse("struct a {\n u8 b\n")).Message);
        Assert.Contains("'repeat' is a word the language uses", Assert.Throws<TemplateException>(() => Template.Parse("u8 repeat")).Message);
        Assert.Contains("aren't hex", Assert.Throws<TemplateException>(() => Template.Parse("magic \"ZZ\"")).Message);
        Assert.Contains("missing its closing \"", Assert.Throws<TemplateException>(() => Template.Parse("name \"x\nu8 a")).Message);
    }

    [Fact]
    public void Repeats_with_no_count_stop_when_nothing_is_read_and_huge_files_stop_at_the_limit()
    {
        var r = RunText("repeat nothing { if 0 { u8 x } }", [1, 2, 3]);
        Assert.Null(r.Error);
        Assert.Single(Node(r.Root, "nothing").Children);

        var many = RunText("repeat b { u8 x }", new byte[TemplateRunner.MaxNodes]);
        Assert.True(many.Truncated);
        Assert.StartsWith("Stopped after", many.Error);
    }

    [Fact]
    public void The_users_own_templates_are_loaded_and_take_the_place_of_a_built_in_one()
    {
        var dir = Directory.CreateTempSubdirectory("avahexedit-templates-").FullName;
        File.WriteAllText(Path.Combine(dir, "mine.hext"), "name \"My format\"\nextensions \"mine\"\nu32 magic");
        File.WriteAllText(Path.Combine(dir, "png.hext"), "name \"PNG image\"\nmagic \"89 50\"\nbytes[8] sig");
        File.WriteAllText(Path.Combine(dir, "broken.hext"), "u8");
        var lib = TemplateLibrary.Load(dir);
        Assert.Equal("My format", lib.Pick(Document.FromBytes([1, 2, 3, 4]), "a.mine")?.Name);
        Assert.Equal(dir, Path.GetDirectoryName(lib.Templates.Single(t => t.Name == "PNG image").Source));
        Assert.Single(lib.Problems);
        Assert.StartsWith("broken.hext, line 1:", lib.Problems[0]);
        Assert.Null(lib.Pick(Document.FromBytes([1, 2, 3, 4]), "a.unknown"));
    }
}
