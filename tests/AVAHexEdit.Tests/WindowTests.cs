using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using AVAHexEdit.Core;

namespace AVAHexEdit.Tests;

/// <summary>
/// The window on Avalonia's headless platform: opening a file and seeing its template, typing over and into it,
/// undoing, saving, finding and replacing, going to an offset, writing a value from the inspector, checksums and
/// comparing. Set AVAHEXEDIT_SHOTS to a folder to save pictures of it (the README's screenshots come from here).
/// </summary>
public class WindowTests
{
    private static string? Shots => Environment.GetEnvironmentVariable("AVAHEXEDIT_SHOTS") is { Length: > 0 } d ? d : null;

    private static string Folder() => Directory.CreateTempSubdirectory("avahexedit-").FullName;

    private static string Sample(string name = "icon.png")
    {
        var path = Path.Combine(Folder(), name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Samples", "icon.png"), path);
        return path;
    }

    private static MainWindow Open(string? file = null, int width = 1400, int height = 860)
    {
        var dir = Folder();
        var w = new MainWindow(Path.Combine(dir, "settings.json"), Path.Combine(dir, "templates"), file) { Width = width, Height = height };
        w.Ask = (_, _, _) => throw new Xunit.Sdk.XunitException("Nothing should have asked.");
        w.Show();
        Pump();
        Wait(w.TemplateRunning);
        return w;
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    /// <summary>Closes the window, dropping any changes left.</summary>
    private static void Done(MainWindow w)
    {
        w.Ask = (_, _, _) => Task.FromResult(1);
        w.Close();
        Pump();
    }

    /// <summary>Runs the window's jobs until a task started there is done.</summary>
    private static T Wait<T>(Task<T> task)
    {
        Wait((Task)task);
        return task.Result;
    }

    private static void Wait(Task task)
    {
        for (var i = 0; i < 1000 && !task.IsCompleted; i++)
        {
            Thread.Sleep(5);
            Pump();
        }
        Assert.True(task.IsCompleted, "It didn't finish in time.");
        task.GetAwaiter().GetResult();
        Pump();
    }

    private static void Press(Window w, Avalonia.Input.Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        w.KeyPress(key, modifiers, PhysicalKey.None, null);
        w.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Pump();
    }

    private static void Type(Window w, string text)
    {
        w.KeyTextInput(text);
        Pump();
    }

    private static void Shot(Window w, string name)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Shots is not { } dir) return;
        Directory.CreateDirectory(dir);
        frame.Save(Path.Combine(dir, $"AVAHexEdit-{name}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    [AvaloniaFact]
    public void Opening_a_png_lays_its_template_over_the_bytes()
    {
        var path = Sample();
        var w = Open(path);
        Assert.Equal(new FileInfo(path).Length, w.Doc.Length);
        Assert.Equal("icon.png — AVAHexEdit", w.Title);
        Assert.NotNull(w.TemplateResult);
        Assert.Equal("PNG image", w.TemplateResult!.Template.Name);
        Assert.Null(w.TemplateResult.Error);
        Assert.Equal(w.TemplateResult.Fields.Count, w.Hex.Tints.Count);
        Assert.StartsWith("signature = 89 50 4E 47", w.FieldInfo.Text);
        Assert.Equal("Offset 0x0 (0)", w.PositionInfo.Text);
        Assert.Equal("137", w.InspectorValues["UInt8"].Text);

        // Picking a part in the tree selects its bytes.
        var chunks = (IEnumerable<MainWindow.NodeItem>)w.TemplateTree.ItemsSource!;
        var chunk = chunks.Single(n => n.Node.Name == "chunk");
        var ihdr = chunk.Children.First();
        var width = ihdr.Children.Single(c => c.Node.Name == "header").Children.Single(c => c.Node.Name == "width");
        w.TemplateTree.SelectedItem = chunk;
        Pump();
        w.TemplateTree.SelectedItem = width;
        Pump();
        Assert.Equal(new ByteRange(16, 4), w.Hex.Selection);
        Assert.Equal(new ByteRange(16, 4), w.Hex.Outline);
        Assert.StartsWith("chunk › chunk[0] › header › width = 256", w.FieldInfo.Text);
        Assert.Equal("65536", w.InspectorValues["UInt32"].Text); // PNG is big-endian; read little-endian it's wrong
        w.BigEndian.IsChecked = true;
        Pump();
        Assert.Equal("256", w.InspectorValues["UInt32"].Text);

        // Going to an offset.
        Assert.True(w.GoTo("21"));
        Assert.Equal(0x21, w.Hex.Caret);
        Assert.True(w.GoTo("+0x10"));
        Assert.Equal(0x31, w.Hex.Caret);
        Assert.False(w.GoTo("nonsense"));
        Assert.Equal(Look.Error, w.Message.Foreground);

        // The picture: the template's tree open at the header, and its width picked.
        w.Panel.SelectedIndex = 1;
        Pump();
        foreach (var item in new[] { chunk, ihdr, ihdr.Children.Single(c => c.Node.Name == "header") })
        {
            ((TreeViewItem)w.TemplateTree.TreeContainerFromItem(item)!).IsExpanded = true;
            Pump();
        }
        w.TemplateTree.SelectedItem = null;
        w.TemplateTree.SelectedItem = width;
        Pump();
        Assert.Equal(new ByteRange(16, 4), w.Hex.Selection);
        w.Hex.Focus();
        w.Note($"Opened {path} (9.9 KB).");
        Pump();
        Shot(w, "png");
        Done(w);
    }

    [AvaloniaFact]
    public void Typing_writes_over_or_inserts_and_undo_takes_it_back_then_saving_writes_the_file()
    {
        var path = Path.Combine(Folder(), "data.bin");
        File.WriteAllBytes(path, [0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77]);
        var w = Open(path);
        Assert.Null(w.TemplateResult); // nothing knows a .bin
        w.Hex.Focus();
        Pump();

        // Writing over: two hex digits make a byte, then on to the next.
        Type(w, "AB");
        Type(w, "c");
        Assert.Equal(0xAB, w.Doc[0]);
        Assert.Equal(0xC1, w.Doc[1]);
        Assert.Equal(1, w.Hex.Caret);
        Assert.True(w.Hex.LowNibble);
        Assert.Equal("data.bin • — AVAHexEdit", w.Title);
        Type(w, "d");
        Assert.Equal(0xCD, w.Doc[1]);
        Assert.Equal(8, w.Doc.Length);

        // Inserting.
        Press(w, Avalonia.Input.Key.Insert);
        Assert.True(w.Hex.InsertMode);
        Assert.Equal("INS", w.ModeInfo.Text);
        Type(w, "EE");
        Assert.Equal(9, w.Doc.Length);
        Assert.Equal(0xEE, w.Doc[2]);
        Assert.Equal(0x22, w.Doc[3]);

        // The text column.
        Press(w, Avalonia.Input.Key.Tab);
        Assert.True(w.Hex.TextColumn);
        Type(w, "Hi");
        Assert.Equal(11, w.Doc.Length);
        Assert.Equal((byte)'H', w.Doc[3]);
        Assert.Equal((byte)'i', w.Doc[4]);

        // Deleting, and selecting with the keyboard.
        Press(w, Avalonia.Input.Key.Back);
        Assert.Equal(10, w.Doc.Length);
        Press(w, Avalonia.Input.Key.Home);
        Press(w, Avalonia.Input.Key.Right, RawInputModifiers.Shift);
        Press(w, Avalonia.Input.Key.Right, RawInputModifiers.Shift);
        Assert.Equal(new ByteRange(0, 2), w.Hex.Selection);
        Press(w, Avalonia.Input.Key.Delete);
        Assert.Equal(8, w.Doc.Length);
        Assert.Equal(0xEE, w.Doc[0]);

        // Undo, step by step, back to the file as it was; then redo one.
        while (w.Doc.CanUndo) Press(w, Avalonia.Input.Key.Z, RawInputModifiers.Control);
        Assert.False(w.Doc.IsModified);
        Assert.Equal([0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77], w.Doc.ReadArray(0, 8));
        Press(w, Avalonia.Input.Key.Y, RawInputModifiers.Control);
        Assert.Equal(0xAB, w.Doc[0]);
        Assert.Equal(0xCD, w.Doc[1]);

        // Typing at the very end makes the file longer.
        Press(w, Avalonia.Input.Key.Tab);
        Press(w, Avalonia.Input.Key.End, RawInputModifiers.Control);
        Assert.Equal(8, w.Hex.Caret);
        Type(w, "9");
        Assert.Equal(9, w.Doc.Length);
        Assert.Equal(0x90, w.Doc[8]);

        Assert.True(Wait(w.Save()));
        Assert.False(w.Doc.IsModified);
        Assert.Equal([0xAB, 0xCD, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x90], File.ReadAllBytes(path));
        Assert.StartsWith("Saved ", w.Message.Text);
        Assert.Equal("data.bin — AVAHexEdit", w.Title);
        Done(w);
    }

    [AvaloniaFact]
    public void Writing_a_value_from_the_inspector_and_pasting_hex()
    {
        var path = Path.Combine(Folder(), "values.bin");
        File.WriteAllBytes(path, new byte[16]);
        var w = Open(path);
        w.Hex.MoveTo(4);
        w.WriteValue("UInt32", "0x12345678");
        Assert.Equal([0x78, 0x56, 0x34, 0x12], w.Doc.ReadArray(4, 4));
        Assert.Equal("305419896", w.InspectorValues["UInt32"].Text);
        w.WriteValue("Int8", "300");
        Assert.Equal(Look.Error, w.Message.Foreground);
        w.BigEndian.IsChecked = true;
        w.WriteValue("Int16", "-2");
        Assert.Equal([0xFF, 0xFE], w.Doc.ReadArray(4, 2));

        w.Hex.MoveTo(10);
        w.Hex.PasteText("DE AD BE EF");
        Assert.Equal([0xDE, 0xAD, 0xBE, 0xEF], w.Doc.ReadArray(10, 4));
        Assert.Equal(14, w.Hex.Caret);
        Assert.Equal(16, w.Doc.Length); // written over, not inserted
        w.Hex.Select(10, 4);
        Assert.Equal("DE AD BE EF", w.Hex.Copied(HexView.CopyFormat.Hex));
        Assert.Equal("3q2+7w==", w.Hex.Copied(HexView.CopyFormat.Base64));
        Done(w);
    }

    [AvaloniaFact]
    public void Find_next_previous_count_and_replace()
    {
        var path = Path.Combine(Folder(), "text.txt");
        File.WriteAllText(path, "one cat, two cats, a Cat and a dog");
        var w = Open(path);
        w.ShowFind(replace: false);
        Pump();
        Assert.True(w.FindBar.IsVisible);
        w.FindModeChoice.SelectedIndex = 1; // text
        w.FindBox.Text = "cat";
        w.MatchCase.IsChecked = false;
        Assert.True(Wait(w.FindNext()));
        Assert.Equal(new ByteRange(4, 3), w.Hex.Selection);
        Assert.True(Wait(w.FindNext()));
        Assert.Equal(new ByteRange(13, 3), w.Hex.Selection);
        Assert.True(Wait(w.FindNext()));
        Assert.Equal(new ByteRange(21, 3), w.Hex.Selection);
        Assert.True(Wait(w.FindNext()));
        Assert.Equal(new ByteRange(4, 3), w.Hex.Selection); // round again
        Assert.Contains("going round", w.FindStatus.Text);
        Assert.True(Wait(w.FindNext(forward: false)));
        Assert.Equal(new ByteRange(21, 3), w.Hex.Selection);
        Assert.Equal(3, Wait(w.CountAll()));
        w.MatchCase.IsChecked = true;
        Assert.Equal(2, Wait(w.CountAll()));

        w.ReplaceBox.Text = "fox";
        Assert.Equal(2, Wait(w.ReplaceAll()));
        w.Hex.Focus();
        Assert.Equal("one fox, two foxs, a Cat and a dog", System.Text.Encoding.ASCII.GetString(w.Doc.ReadArray(0, (int)w.Doc.Length)));
        Press(w, Avalonia.Input.Key.Z, RawInputModifiers.Control);
        Assert.Equal("one cat, two cats, a Cat and a dog", System.Text.Encoding.ASCII.GetString(w.Doc.ReadArray(0, (int)w.Doc.Length)));

        // Hex, with a wildcard, and replacing one at a time.
        w.FindModeChoice.SelectedIndex = 0;
        w.FindBox.Text = "61 ?? 64"; // a?d
        w.ReplaceBox.Text = "41 4E 44";
        w.Hex.MoveTo(0);
        Wait(w.ReplaceOne()); // finds the first
        Assert.Equal(new ByteRange(25, 3), w.Hex.Selection);
        Wait(w.ReplaceOne()); // replaces it
        Assert.Equal("AND", System.Text.Encoding.ASCII.GetString(w.Doc.ReadArray(25, 3)));

        w.FindBox.Text = "ZZ";
        Assert.False(Wait(w.FindNext()));
        Assert.Equal(Look.Error, w.FindStatus.Foreground);
        Press(w, Avalonia.Input.Key.Escape);
        Assert.False(w.FindBar.IsVisible);
        Done(w);
    }

    [AvaloniaFact]
    public void Checksums_of_the_file_and_of_a_selection()
    {
        var path = Path.Combine(Folder(), "check.txt");
        File.WriteAllText(path, "123456789");
        var w = Open(path);
        Wait(w.ComputeChecksums());
        Assert.Equal("CBF43926", w.ChecksumValues[ChecksumKind.Crc32].Text);
        Assert.Equal("Of the whole file (9 bytes).", w.ChecksumStatus.Text);
        w.Hex.Select(2, 3);
        w.ChecksumSelection.IsChecked = true;
        Wait(w.ComputeChecksums());
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData("345"u8)), w.ChecksumValues[ChecksumKind.Md5].Text);
        Assert.StartsWith("Of 3 bytes from 0x2", w.ChecksumStatus.Text);
        w.Panel.SelectedIndex = 2;
        Pump();
        Shot(w, "checksums");
        Done(w);
    }

    [AvaloniaFact]
    public void Comparing_with_another_file_marks_each_difference()
    {
        var a = Sample();
        var bytes = File.ReadAllBytes(a);
        bytes[0x40] ^= 0xFF;
        bytes[0x41] ^= 0xFF;
        bytes[0x200] ^= 0x01;
        var b = Path.Combine(Folder(), "icon-changed.png");
        File.WriteAllBytes(b, [.. bytes, 1, 2, 3]);
        var w = Open(a);
        var c = w.CompareWith(b);
        Assert.NotNull(c);
        c!.Width = 1500;
        c.Height = 760;
        Wait(c.Comparing);
        Assert.Equal([new ByteRange(0x40, 2), new ByteRange(0x200, 1), new ByteRange(bytes.Length, 3)], c.Result!.Differences);
        Assert.StartsWith("3 differences, 6 bytes; the second file is 3 bytes longer.", c.Summary.Text);
        Assert.Equal(new ByteRange(0x40, 2), c.Left.View.Selection);
        c.Step(1);
        Assert.Equal(new ByteRange(0x200, 1), c.Right.View.Selection);
        Assert.Equal(c.Left.View.TopRow, c.Right.View.TopRow);
        c.Step(-1);
        Pump();
        Shot(c, "compare");
        c.Close();
        Pump();
        Done(w);
    }

    [AvaloniaFact]
    public void Unsaved_changes_are_asked_about_before_they_could_be_lost()
    {
        var path = Path.Combine(Folder(), "keep.bin");
        File.WriteAllBytes(path, [1, 2, 3]);
        var w = Open(path);
        w.Hex.Focus();
        Type(w, "FF");
        var asked = 0;
        w.Ask = (_, message, buttons) =>
        {
            asked++;
            Assert.Equal("Save the changes to keep.bin?", message);
            Assert.Equal(["Save", "Don't save", "Cancel"], buttons);
            return Task.FromResult(asked == 1 ? 2 : 0); // cancel, then save
        };
        Assert.False(Wait(w.ConfirmDiscard()));
        Assert.True(w.Doc.IsModified);
        Assert.True(Wait(w.ConfirmDiscard()));
        Assert.Equal([0xFF, 2, 3], File.ReadAllBytes(path));
        Assert.Equal(2, asked);
        Done(w);
    }

    [AvaloniaFact]
    public void A_file_that_cant_be_opened_says_why_and_the_window_keeps_going()
    {
        var w = Open();
        Assert.False(w.OpenFile(Path.Combine(Folder(), "missing.bin")));
        Assert.Equal(Look.Error, w.Message.Foreground);
        Assert.StartsWith("Couldn't open", w.Message.Text);
        Assert.Equal("Untitled — AVAHexEdit", w.Title);
        // A new document can be typed into straight away.
        w.Hex.Focus();
        Type(w, "48656C6C6F");
        Assert.Equal("Hello", System.Text.Encoding.ASCII.GetString(w.Doc.ReadArray(0, 5)));
        Done(w);
    }

    [AvaloniaFact]
    public void Clicking_puts_the_caret_on_the_byte_and_dragging_selects()
    {
        var path = Path.Combine(Folder(), "click.bin");
        File.WriteAllBytes(path, Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
        var w = Open(path);
        var hex = w.Hex;
        // Find where byte 0x23 (row 2, column 3) is drawn by asking the view, then click there.
        var origin = hex.TranslatePoint(new Point(0, 0), w)!.Value;
        Point Cell(long offset, bool text)
        {
            for (var x = 0.0; x < hex.Bounds.Width; x += 2)
                for (var y = 20.0; y < hex.Bounds.Height; y += 4)
                {
                    var (o, t, low) = hex.HitTest(new Point(x, y));
                    if (o == offset && t == text && !low) return new Point(x + 2, y + 2) + origin;
                }
            throw new Xunit.Sdk.XunitException("Not on screen.");
        }
        var p = Cell(0x23, text: false);
        w.MouseDown(p, MouseButton.Left);
        w.MouseUp(p, MouseButton.Left);
        Pump();
        Assert.Equal(0x23, hex.Caret);
        Assert.False(hex.TextColumn);

        var q = Cell(0x30, text: true);
        w.MouseDown(q, MouseButton.Left);
        w.MouseMove(Cell(0x35, text: true), RawInputModifiers.LeftMouseButton);
        w.MouseUp(Cell(0x35, text: true), MouseButton.Left);
        Pump();
        Assert.True(hex.TextColumn);
        Assert.Equal(new ByteRange(0x30, 6), hex.Selection);
        Done(w);
    }

    [AvaloniaFact]
    public void A_huge_file_shows_and_scrolls_to_its_end()
    {
        var path = Path.Combine(Folder(), "huge.bin");
        const long size = 5L * 1024 * 1024 * 1024;
        using (var f = new FileStream(path, FileMode.CreateNew)) f.SetLength(size);
        try
        {
            var w = Open(path);
            Assert.Equal("5 GB (5,368,709,120 bytes)", w.LengthInfo.Text);
            w.Hex.Focus();
            Press(w, Avalonia.Input.Key.End, RawInputModifiers.Control);
            Assert.Equal(size, w.Hex.Caret);
            Assert.Equal(w.Hex.MaxTop, w.Hex.TopRow);
            Assert.Equal(w.Hex.MaxTop, (long)w.Pane.Scroll.Value);
            Type(w, "7F");
            Assert.Equal(size + 1, w.Doc.Length);
            Shot(w, "huge");
            Assert.True(w.GoTo("80000000"));
            Assert.Equal(0x80000000, w.Hex.Caret);
            Done(w);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
