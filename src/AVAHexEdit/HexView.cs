using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using AVAHexEdit.Core;

namespace AVAHexEdit;

/// <summary>A run of bytes to tint, in one of the template's field colours.</summary>
public readonly record struct Tint(long Start, long Length, int Colour)
{
    public long End => Start + Length;
}

/// <summary>
/// The hex view and editor: offsets down the left, the bytes in hex, and the same bytes as text, a row at a time, with
/// a caret in either column, a selection, the template's fields tinted, differences from another file marked, and
/// the bytes changed since opening in orange. Draws only the rows on screen, reading them from the document as it
/// draws, so it costs the same for a file of any size. Typing hex digits (or text, in the text column) writes over or
/// inserts; the rest of the editing keys are here too.
/// </summary>
public sealed class HexView : Control
{
    private Document? _document;
    private long _caret, _anchor;
    private long _top;
    private int _bytesPerRow = 16;
    private bool _lowNibble;
    private bool _typing;
    private long _dragOrigin = -1;

    private readonly Typeface _face = new(Look.Mono);
    private double _cw, _lh;
    private const double FontSize = 13.5;
    private const double Pad = 10;

    public HexView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        Measure();
    }

    // What it shows

    public Document? Document
    {
        get => _document;
        set
        {
            if (_document != null) _document.Changed -= OnDocumentChanged;
            _document = value;
            if (_document != null) _document.Changed += OnDocumentChanged;
            _caret = _anchor = 0;
            _top = 0;
            _lowNibble = false;
            _typing = false;
            Tints = [];
            Differences = [];
            Outline = null;
            InvalidateVisual();
            ScrollChanged?.Invoke();
            CaretMoved?.Invoke();
        }
    }

    public long Length => _document?.Length ?? 0;

    public int BytesPerRow
    {
        get => _bytesPerRow;
        set
        {
            _bytesPerRow = Math.Clamp(value, 4, 64);
            EnsureVisible(_caret);
            InvalidateVisual();
            ScrollChanged?.Invoke();
        }
    }

    public bool HexOffsets { get; set { field = value; InvalidateVisual(); } } = true;
    public TextEncodingChoice Encoding { get; set { field = value; InvalidateVisual(); } }

    /// <summary>Typing inserts rather than writing over.</summary>
    public bool InsertMode { get; set { field = value; EndTyping(); InvalidateVisual(); ModeChanged?.Invoke(); } }

    /// <summary>Nothing can be typed, pasted or deleted (the compare window's views).</summary>
    public bool ReadOnly { get; set; }

    /// <summary>The caret is in the text column rather than the hex.</summary>
    public bool TextColumn { get; set { field = value; _lowNibble = false; EndTyping(); InvalidateVisual(); } }

    /// <summary>What is typed next starts a new step to undo.</summary>
    private void EndTyping()
    {
        _typing = false;
        _document?.EndMerge();
    }

    /// <summary>The template's fields, in order of where they start.</summary>
    public IReadOnlyList<Tint> Tints { get; set { field = value; InvalidateVisual(); } } = [];

    /// <summary>Where this file differs from the one it is compared with, in order.</summary>
    public IReadOnlyList<ByteRange> Differences { get; set { field = value; InvalidateVisual(); } } = [];

    /// <summary>A run of bytes boxed (the template field picked in the tree).</summary>
    public ByteRange? Outline { get; set { field = value; InvalidateVisual(); } }

    public event Action? CaretMoved;
    public event Action? ScrollChanged;
    public event Action? ModeChanged;

    /// <summary>Something went wrong that the window should show (a paste that wasn't possible).</summary>
    public event Action<string>? Problem;

    // Where things are

    public long Caret => _caret;
    public long Anchor => _anchor;
    public bool LowNibble => _lowNibble;
    public bool HasSelection => _caret != _anchor;
    public ByteRange Selection => new(Math.Min(_caret, _anchor), Math.Abs(_caret - _anchor));

    /// <summary>Where the inspector reads: the start of the selection, or the caret.</summary>
    public long InspectAt => HasSelection ? Selection.Start : _caret;

    public long TopRow
    {
        get => _top;
        set
        {
            var t = Math.Clamp(value, 0, MaxTop);
            if (t == _top) return;
            _top = t;
            InvalidateVisual();
            ScrollChanged?.Invoke();
        }
    }

    /// <summary>Rows in the file, with one more when the last is full, for the caret at the very end.</summary>
    public long RowCount => Length / _bytesPerRow + 1;

    public int VisibleRows => Math.Max(1, (int)((Bounds.Height - HeaderHeight) / _lh));

    public long MaxTop => Math.Max(0, RowCount - VisibleRows);

    private double HeaderHeight => _lh + 6;

    private void Measure()
    {
        var probe = Text(new string('0', 32), Look.Text);
        _cw = probe.WidthIncludingTrailingWhitespace / 32;
        _lh = Math.Ceiling(probe.Height + 3);
    }

    private FormattedText Text(string s, IBrush brush, double size = FontSize) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, size, brush);

    private int OffsetDigits => HexOffsets
        ? Math.Max(8, (Length.ToString("X", CultureInfo.InvariantCulture)).Length)
        : Math.Max(8, Length.ToString(CultureInfo.InvariantCulture).Length);

    private double HexLeft => Pad + (OffsetDigits + 2) * _cw;

    private double HexX(int col) => HexLeft + (col * 3 + col / 8) * _cw;

    private double HexWidth => (_bytesPerRow * 3 + (_bytesPerRow - 1) / 8 - 1) * _cw;

    private double TextLeft => HexLeft + HexWidth + 3 * _cw;

    private double TextX(int col) => TextLeft + col * _cw;

    private double RowY(long row) => HeaderHeight + (row - _top) * _lh;

    /// <summary>The width the view needs to show a row in full.</summary>
    public double NeededWidth => TextLeft + _bytesPerRow * _cw + Pad;

    // Drawing

    public override void Render(DrawingContext g)
    {
        var bounds = new Rect(Bounds.Size);
        g.FillRectangle(Look.Sheet, bounds);
        var rows = VisibleRows + 1;
        var bpr = _bytesPerRow;

        // The header: the column numbers over the hex and the text.
        g.FillRectangle(Look.Bar, new Rect(0, 0, bounds.Width, HeaderHeight - 2));
        var header = new System.Text.StringBuilder();
        for (var c = 0; c < bpr; c++)
        {
            header.Append((HexOffsets ? c.ToString("X2", CultureInfo.InvariantCulture) : (c % 100).ToString("D2", CultureInfo.InvariantCulture)));
            header.Append(c % 8 == 7 && c < bpr - 1 ? "  " : " ");
        }
        g.DrawText(Text(header.ToString().TrimEnd(), Look.Heading), new Point(HexLeft, 3));
        var textHeader = new string([.. Enumerable.Range(0, bpr).Select(c => "0123456789ABCDEF"[c % 16])]);
        g.DrawText(Text(textHeader, Look.Heading), new Point(TextLeft, 3));
        g.DrawText(Text(HexOffsets ? "Offset (h)" : "Offset", Look.Heading, 11), new Point(Pad, 5));
        g.DrawLine(new Pen(Look.Border, 1), new Point(0, HeaderHeight - 2.5), new Point(bounds.Width, HeaderHeight - 2.5));
        var sep = new Pen(Look.Border, 1);
        g.DrawLine(sep, new Point(TextLeft - 1.5 * _cw, 0), new Point(TextLeft - 1.5 * _cw, bounds.Height));
        g.DrawLine(sep, new Point(HexLeft - 1.2 * _cw, 0), new Point(HexLeft - 1.2 * _cw, bounds.Height));

        if (_document is null) return;
        var first = _top * bpr;
        var count = (int)Math.Clamp(Length - first, 0, (long)rows * bpr);
        var bytes = new byte[count];
        var changed = new bool[count];
        _document.Read(first, bytes, changed);
        var selection = Selection;
        var tintIndex = FirstTint(first);
        var diffIndex = FirstDifference(first);

        using (g.PushClip(new Rect(0, HeaderHeight - 2, bounds.Width, bounds.Height)))
        {
            for (var r = 0; r < rows; r++)
            {
                var row = _top + r;
                var rowStart = row * bpr;
                if (rowStart > Length) break;
                var y = RowY(row);
                if (row % 2 == 1) g.FillRectangle(Look.RowStripe, new Rect(0, y, bounds.Width, _lh));
                var n = (int)Math.Clamp(Length - rowStart, 0, bpr);

                // Backgrounds: template fields, then differences, then the selection on top.
                for (var c = 0; c < n; c++)
                {
                    var at = rowStart + c;
                    IBrush? back = null;
                    while (tintIndex < Tints.Count && Tints[tintIndex].End <= at) tintIndex++;
                    if (tintIndex < Tints.Count && Tints[tintIndex].Start <= at) back = Look.Fields[Tints[tintIndex].Colour % Look.Fields.Length];
                    while (diffIndex < Differences.Count && Differences[diffIndex].End <= at) diffIndex++;
                    if (diffIndex < Differences.Count && Differences[diffIndex].Start <= at) back = Look.Difference;
                    if (selection.Contains(at)) back = Look.Selection;
                    if (back is null) continue;
                    // Fill to the next byte's cell (the gap between), unless it is the last of its colour.
                    var hx = HexX(c) - _cw * 0.5;
                    var hw = (c < bpr - 1 ? HexX(c + 1) - _cw * 0.5 : HexX(c) + 2.5 * _cw) - hx;
                    g.FillRectangle(back, new Rect(hx, y, hw, _lh));
                    g.FillRectangle(back, new Rect(TextX(c), y, _cw, _lh));
                }

                // The offset.
                var label = ByteFormats.Offset(rowStart, HexOffsets, OffsetDigits);
                g.DrawText(Text(label, Look.Gutter), new Point(Pad, y + 1.5));
                if (n == 0) continue;

                // The hex and the text, each byte coloured by what it is.
                var hex = new char[n * 3 + (n - 1) / 8];
                Array.Fill(hex, ' ');
                var text = new char[n];
                for (var c = 0; c < n; c++)
                {
                    var b = bytes[r * bpr + c];
                    var pos = c * 3 + c / 8;
                    hex[pos] = "0123456789ABCDEF"[b >> 4];
                    hex[pos + 1] = "0123456789ABCDEF"[b & 15];
                    text[c] = ByteFormats.Shown(b, Encoding);
                }
                var hexText = Text(new string(hex), Look.Printable);
                var textText = Text(new string(text), Look.Printable);
                for (var c = 0; c < n; c++)
                {
                    var i = r * bpr + c;
                    var brush = selection.Contains(rowStart + c) ? Look.Selected : changed[i] ? Look.Changed : BrushFor(bytes[i]);
                    if (ReferenceEquals(brush, Look.Printable)) continue;
                    hexText.SetForegroundBrush(brush, c * 3 + c / 8, 2);
                    textText.SetForegroundBrush(brush, c, 1);
                }
                g.DrawText(hexText, new Point(HexLeft, y + 1.5));
                g.DrawText(textText, new Point(TextLeft, y + 1.5));
            }

            if (Outline is { Length: > 0 } o) DrawOutline(g, o);
            DrawCaret(g);
        }
    }

    private static IBrush BrushFor(byte b) => b switch
    {
        0 => Look.Zero,
        < 0x20 or 0x7F => Look.Control,
        >= 0x80 => Look.High,
        _ => Look.Printable,
    };

    private int FirstTint(long offset)
    {
        int lo = 0, hi = Tints.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (Tints[mid].End <= offset) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private int FirstDifference(long offset)
    {
        int lo = 0, hi = Differences.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (Differences[mid].End <= offset) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private void DrawOutline(DrawingContext g, ByteRange o)
    {
        var pen = new Pen(Look.Accent, 1.5);
        var bpr = _bytesPerRow;
        var firstRow = Math.Max(o.Start / bpr, _top);
        var lastRow = Math.Min((o.End - 1) / bpr, _top + VisibleRows);
        for (var row = firstRow; row <= lastRow; row++)
        {
            var c0 = (int)(row == o.Start / bpr ? o.Start % bpr : 0);
            var c1 = (int)(row == (o.End - 1) / bpr ? (o.End - 1) % bpr : bpr - 1);
            var y = RowY(row);
            g.DrawRectangle(null, pen, new Rect(HexX(c0) - _cw * 0.5, y, HexX(c1) + 2.5 * _cw - (HexX(c0) - _cw * 0.5), _lh));
            g.DrawRectangle(null, pen, new Rect(TextX(c0), y, (c1 - c0 + 1) * _cw, _lh));
        }
    }

    private void DrawCaret(DrawingContext g)
    {
        var row = _caret / _bytesPerRow;
        if (row < _top || row > _top + VisibleRows) return;
        var col = (int)(_caret % _bytesPerRow);
        var y = RowY(row);
        var focused = IsFocused;
        var hexX = HexX(col) + (_lowNibble && !TextColumn ? _cw : 0);
        var textX = TextX(col);
        if (InsertMode)
        {
            // A bar before the byte: what is typed goes in here.
            var bar = focused ? Look.Caret : Look.CaretGhost;
            g.FillRectangle(TextColumn ? Look.CaretGhost : bar, new Rect(HexX(col) - 2, y, 2, _lh));
            g.FillRectangle(TextColumn ? bar : Look.CaretGhost, new Rect(textX - 1, y, 2, _lh));
            return;
        }
        // A block under the digit (or character) that typing replaces; an outline in the other column.
        var main = new Rect(TextColumn ? textX : hexX, y + _lh - 3, _cw, 2.5);
        var other = TextColumn ? new Rect(HexX(col), y, 2 * _cw, _lh) : new Rect(textX, y, _cw, _lh);
        g.FillRectangle(focused ? Look.Caret : Look.CaretGhost, main);
        g.DrawRectangle(null, new Pen(Look.CaretGhost, 1), other);
        if (focused) g.FillRectangle(Look.CaretGhost, new Rect(TextColumn ? textX : hexX, y, _cw, _lh));
    }

    // Moving about

    /// <summary>Moves the caret, extending the selection from the anchor or (by default) dropping it.</summary>
    public void MoveTo(long offset, bool extend = false)
    {
        _caret = Math.Clamp(offset, 0, Length);
        if (!extend) _anchor = _caret;
        _lowNibble = false;
        _typing = false;
        _document?.EndMerge();
        EnsureVisible(_caret);
        InvalidateVisual();
        CaretMoved?.Invoke();
    }

    /// <summary>Selects a run of bytes, scrolled into view, with the caret after it.</summary>
    public void Select(long start, long length)
    {
        start = Math.Clamp(start, 0, Length);
        var end = Math.Clamp(start + length, start, Length);
        EnsureVisible(end > start ? end - 1 : end);
        EnsureVisible(start);
        _anchor = start;
        _caret = end;
        _lowNibble = false;
        _typing = false;
        _document?.EndMerge();
        InvalidateVisual();
        CaretMoved?.Invoke();
    }

    public void SelectAll() => Select(0, Length);

    /// <summary>Scrolls so <paramref name="offset"/>'s row is on screen.</summary>
    public void EnsureVisible(long offset)
    {
        var row = offset / _bytesPerRow;
        if (row < _top) TopRow = row;
        else if (row >= _top + VisibleRows) TopRow = row - VisibleRows + 1;
    }

    /// <summary>The byte under a point, and whether it is in the text column; the end of the file past the last byte.</summary>
    public (long Offset, bool Text, bool Low) HitTest(Point p)
    {
        var row = _top + (long)Math.Floor((p.Y - HeaderHeight) / _lh);
        row = Math.Clamp(row, 0, RowCount - 1);
        bool text = p.X >= TextLeft - _cw;
        int col;
        var low = false;
        if (text)
        {
            col = (int)Math.Floor((p.X - TextLeft) / _cw);
        }
        else
        {
            // Each byte's cell spans its two digits and half the gaps either side.
            col = 0;
            for (var c = 0; c < _bytesPerRow; c++)
                if (p.X >= HexX(c) - _cw * 0.5) col = c;
            low = p.X >= HexX(col) + _cw;
        }
        col = Math.Clamp(col, 0, _bytesPerRow - 1);
        return (Math.Min(row * _bytesPerRow + col, Length), text, low);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        var (at, text, low) = HitTest(e.GetPosition(this));
        if (e.GetPosition(this).Y < HeaderHeight) return;
        TextColumn = text;
        var extend = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        MoveTo(at, extend);
        if (!extend && !text && low && at < Length) _lowNibble = true;
        _dragOrigin = extend ? -1 : at;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragOrigin < 0 || !Equals(e.Pointer.Captured, this)) return;
        var p = e.GetPosition(this);
        // Scroll when dragging above or below.
        if (p.Y < HeaderHeight) TopRow -= 1;
        else if (p.Y > Bounds.Height) TopRow += 1;
        var (at, _, _) = HitTest(p);
        if (at == _dragOrigin && _caret == _anchor) return;
        // Both the byte pressed on and the one under the pointer are selected.
        if (at >= _dragOrigin) { _anchor = _dragOrigin; _caret = Math.Min(at + 1, Length); }
        else { _anchor = Math.Min(_dragOrigin + 1, Length); _caret = at; }
        _lowNibble = false;
        _typing = false;
        InvalidateVisual();
        CaretMoved?.Invoke();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragOrigin = -1;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        TopRow -= (long)Math.Round(e.Delta.Y * 3);
        e.Handled = true;
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_top > MaxTop) TopRow = MaxTop;
        ScrollChanged?.Invoke();
    }

    // Keys

    private static bool Command(KeyModifiers m) => (m & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _document is null) return;
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        var command = Command(e.KeyModifiers);
        var bpr = _bytesPerRow;
        var page = (long)Math.Max(1, VisibleRows - 1) * bpr;
        // With a selection and no Shift, Left and Up go to its start, Right and Down to its end.
        var from = _caret;
        switch (e.Key)
        {
            case Key.Left: MoveTo(!shift && HasSelection ? Selection.Start : (command ? from - from % bpr : from - 1), shift); break;
            case Key.Right: MoveTo(!shift && HasSelection ? Selection.End : (command ? from - from % bpr + bpr - 1 : from + 1), shift); break;
            case Key.Up:
                if (command) TopRow -= 1;
                else MoveTo(from - bpr >= 0 ? from - bpr : from, shift);
                break;
            case Key.Down:
                if (command) TopRow += 1;
                else MoveTo(from + bpr <= Length ? from + bpr : from, shift);
                break;
            case Key.PageUp:
                TopRow -= VisibleRows - 1;
                MoveTo(Math.Max(from % bpr, from - page), shift);
                break;
            case Key.PageDown:
                TopRow += VisibleRows - 1;
                MoveTo(from + page <= Length ? from + page : Length, shift);
                break;
            case Key.Home: MoveTo(command ? 0 : from - from % bpr, shift); break;
            case Key.End: MoveTo(command ? Length : Math.Min(from - from % bpr + bpr - 1, Length), shift); break;
            case Key.Tab:
                TextColumn = !TextColumn;
                break;
            case Key.Insert when !command && !shift:
                InsertMode = !InsertMode;
                break;
            case Key.Back: Erase(back: true); break;
            case Key.Delete: Erase(back: false); break;
            case Key.A when command: SelectAll(); break;
            case Key.Z when command && shift: Redo(); break;
            case Key.Z when command: Undo(); break;
            case Key.Y when command: Redo(); break;
            case Key.C when command: _ = Copy(TextColumn ? CopyFormat.Text : CopyFormat.Hex); break;
            case Key.X when command: _ = Cut(); break;
            case Key.V when command: _ = Paste(); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (string.IsNullOrEmpty(e.Text) || _document is null || ReadOnly) return;
        foreach (var ch in e.Text)
        {
            if (char.IsControl(ch)) continue;
            if (TextColumn) TypeText(ch.ToString());
            else if (char.IsAsciiHexDigit(ch)) TypeNibble(Convert.ToInt32(ch.ToString(), 16));
        }
        e.Handled = true;
    }

    // Editing

    /// <summary>Clears the way for typing: with a selection, inserting takes it out; writing over starts at its start.</summary>
    private void BeforeTyping()
    {
        if (!HasSelection) return;
        var s = Selection;
        if (InsertMode) _document!.Delete(s.Start, s.Length);
        _caret = _anchor = s.Start;
        _lowNibble = false;
        _typing = false;
    }

    /// <summary>A hex digit typed at the caret: the high half of the byte, then the low half, then on to the next.</summary>
    public void TypeNibble(int value)
    {
        if (_document is null || ReadOnly) return;
        BeforeTyping();
        var d = _document;
        if (!_lowNibble)
        {
            var b = (byte)(value << 4);
            if (InsertMode || _caret >= d.Length) d.Insert(_caret, [b], _typing);
            else d.Overwrite(_caret, [(byte)(b | (d[_caret] & 0x0F))], _typing);
            _lowNibble = true;
        }
        else
        {
            d.Overwrite(_caret, [(byte)((d[_caret] & 0xF0) | value)], merge: true);
            _lowNibble = false;
            _caret++;
        }
        _anchor = _caret;
        _typing = true;
        EnsureVisible(_caret);
        InvalidateVisual();
        CaretMoved?.Invoke();
    }

    /// <summary>Text typed in the text column, as bytes in the chosen encoding.</summary>
    public void TypeText(string text)
    {
        if (_document is null || ReadOnly) return;
        BeforeTyping();
        var bytes = ByteFormats.FromText(text, Encoding);
        if (InsertMode) _document.Insert(_caret, bytes, _typing);
        else _document.Overwrite(_caret, bytes, _typing);
        _caret += bytes.Length;
        _anchor = _caret;
        _lowNibble = false;
        _typing = true;
        EnsureVisible(_caret);
        InvalidateVisual();
        CaretMoved?.Invoke();
    }

    /// <summary>Delete (the byte after the caret) or Backspace (the one before); either takes out a selection.</summary>
    public void Erase(bool back)
    {
        if (_document is null || ReadOnly) return;
        if (HasSelection)
        {
            var s = Selection;
            _document.Delete(s.Start, s.Length);
            MoveTo(s.Start);
            return;
        }
        if (back && _caret > 0)
        {
            _document.Delete(_caret - 1, 1);
            MoveTo(_caret - 1);
        }
        else if (!back && _caret < Length)
        {
            _document.Delete(_caret, 1);
            MoveTo(_caret);
        }
    }

    /// <summary>Writes bytes at the caret (over, or in, by the mode), replacing a selection; the caret ends after them.</summary>
    public void Put(byte[] bytes)
    {
        if (_document is null || ReadOnly || bytes.Length == 0) return;
        var s = Selection;
        var at = HasSelection ? s.Start : _caret;
        if (HasSelection && (InsertMode || bytes.Length != s.Length)) _document.Replace(at, s.Length, bytes);
        else if (InsertMode) _document.Insert(at, bytes);
        else _document.Overwrite(at, bytes);
        _document.EndMerge();
        MoveTo(at + bytes.Length);
    }

    public void Undo()
    {
        if (!ReadOnly && _document?.Undo() is { } where) MoveTo(where);
    }

    public void Redo()
    {
        if (!ReadOnly && _document?.Redo() is { } where) MoveTo(where);
    }

    private void OnDocumentChanged()
    {
        if (_caret > Length) _caret = Length;
        if (_anchor > Length) _anchor = Length;
        if (_top > MaxTop) _top = MaxTop;
        InvalidateVisual();
        ScrollChanged?.Invoke();
    }

    // The clipboard

    public enum CopyFormat { Hex, Text, CArray, Base64 }

    /// <summary>The most bytes copied at once (as hex, 16 MB of bytes is 48 MB of text).</summary>
    public const int MaxCopy = 16 * 1024 * 1024;

    /// <summary>The selection (or the byte at the caret) as text in a format.</summary>
    public string? Copied(CopyFormat format)
    {
        if (_document is null) return null;
        var s = HasSelection ? Selection : new ByteRange(_caret, Math.Min(1, Length - _caret));
        if (s.Length == 0) return null;
        if (s.Length > MaxCopy)
        {
            Problem?.Invoke($"That's too much to copy: {ByteFormats.Size(s.Length)} (the most is {ByteFormats.Size(MaxCopy)}).");
            return null;
        }
        var bytes = _document.ReadArray(s.Start, (int)s.Length);
        return format switch
        {
            CopyFormat.Text => ByteFormats.ToText(bytes, Encoding),
            CopyFormat.CArray => ByteFormats.ToCArray(bytes),
            CopyFormat.Base64 => ByteFormats.ToBase64(bytes),
            _ => ByteFormats.ToHex(bytes),
        };
    }

    public async Task Copy(CopyFormat format)
    {
        if (Copied(format) is not { } text || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(text);
    }

    public async Task Cut()
    {
        if (ReadOnly || !HasSelection) return;
        await Copy(TextColumn ? CopyFormat.Text : CopyFormat.Hex);
        Erase(back: false);
    }

    public async Task Paste()
    {
        if (ReadOnly || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text)) return;
        PasteText(text);
    }

    /// <summary>
    /// Pasted text: in the hex column, hex digits become those bytes (anything else is pasted as text); in the text
    /// column, it is the text's bytes.
    /// </summary>
    public void PasteText(string text)
    {
        if (!TextColumn && ByteFormats.TryParseHex(text, out var bytes)) Put(bytes);
        else Put(ByteFormats.FromText(text, Encoding));
    }
}
