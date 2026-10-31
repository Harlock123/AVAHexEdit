using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AVAHexEdit.Core;

/// <summary>One named part of a file, as a template lays it out: a field, or a struct or repeat holding more.</summary>
public sealed class TemplateNode
{
    public required string Name { get; init; }
    public long Offset { get; set; }
    public long Length { get; set; }

    /// <summary>"u32", "bytes[12]", "struct"...</summary>
    public string Type { get; init; } = "";

    /// <summary>What it holds, as shown: "1024 (0x400)", "\"IHDR\"", "89 50 4E 47…".</summary>
    public string Value { get; set; } = "";

    public List<TemplateNode> Children { get; } = [];
    public TemplateNode? Parent { get; init; }

    /// <summary>A field (rather than a struct or repeat): the parts coloured in the hex view.</summary>
    public bool IsField { get; init; }

    /// <summary>Which of the field colours it is drawn in, so that neighbours differ.</summary>
    public int Colour { get; set; }

    public long End => Offset + Length;

    /// <summary>"chunk[2] › data".</summary>
    public string Path
    {
        get
        {
            var parts = new List<string>();
            for (var n = this; n?.Parent != null; n = n.Parent) parts.Add(n.Name);
            parts.Reverse();
            return string.Join(" › ", parts);
        }
    }

    public override string ToString() => Value.Length > 0 ? $"{Name} = {Value}" : Name;
}

/// <summary>What running a template over a file gave: the tree of parts, its fields in order, and what stopped it, if anything.</summary>
public sealed record TemplateResult(Template Template, TemplateNode Root, List<TemplateNode> Fields, string? Error, bool Truncated)
{
    /// <summary>The field holding the byte at <paramref name="offset"/>, if any.</summary>
    public TemplateNode? FieldAt(long offset)
    {
        int lo = 0, hi = Fields.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (Fields[mid].Offset <= offset) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        // Fields can overlap when a template seeks back; look at a few before too.
        for (var i = found; i >= 0 && i > found - 8; i--)
            if (offset >= Fields[i].Offset && offset < Fields[i].End) return Fields[i];
        return null;
    }
}

/// <summary>
/// Lays a <see cref="Template"/> over a file's bytes. It stops (keeping everything found so far) at the first field
/// that runs past the end of what holds it, after <see cref="MaxNodes"/> parts, or when it has taken too long.
/// </summary>
public static class TemplateRunner
{
    public const int MaxNodes = 200_000;

    /// <summary>Numeric arrays longer than this show as one field, not a part for each value.</summary>
    public const int MaxArrayChildren = 1024;

    public static TemplateResult Run(Template template, IBytes data, CancellationToken cancel = default, TimeSpan? budget = null)
    {
        var root = new TemplateNode { Name = template.Name, Type = "file", Offset = 0, Length = data.Length };
        var run = new Runner(data, template.BigEndian, cancel, budget ?? TimeSpan.FromSeconds(5));
        string? error = null;
        var truncated = false;
        try
        {
            run.Exec(template.Body, root, new Scope(null));
        }
        catch (Stop s)
        {
            error = s.Message;
            truncated = s.Truncated;
        }
        var fields = new List<TemplateNode>();
        Collect(root, fields);
        fields.Sort((a, b) => a.Offset != b.Offset ? a.Offset.CompareTo(b.Offset) : b.Length.CompareTo(a.Length));
        for (var i = 0; i < fields.Count; i++) fields[i].Colour = i % 6;
        return new TemplateResult(template, root, fields, error, truncated);
    }

    private static void Collect(TemplateNode n, List<TemplateNode> into)
    {
        foreach (var c in n.Children)
        {
            if (c.IsField)
            {
                if (c.Length > 0) into.Add(c); // an empty field has no bytes to colour or find
            }
            else Collect(c, into);
        }
    }

    /// <summary>Stops the run: a field that runs past the end, a mistake found while running, or too much to show.</summary>
    private sealed class Stop(string message, bool truncated = false) : Exception(message)
    {
        public bool Truncated { get; } = truncated;
    }

    /// <summary>The fields read so far in one struct or repeat, by name, looked up from the inside out.</summary>
    private sealed class Scope(Scope? parent)
    {
        public readonly Dictionary<string, object> Values = [];
        public readonly Dictionary<string, string> Shown = [];
        public Scope? Parent { get; } = parent;

        public object? Find(string name)
        {
            for (var s = this; s != null; s = s.Parent)
                if (s.Values.TryGetValue(name, out var v)) return v;
            return null;
        }
    }

    private sealed class Runner(IBytes data, bool bigEndian, CancellationToken cancel, TimeSpan budget)
    {
        private long _pos;
        private bool _big = bigEndian;
        private int _nodes;
        private readonly Stack<long> _limits = new([data.Length]);
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private long Limit => _limits.Peek();

        private TemplateNode Add(TemplateNode parent, TemplateNode node)
        {
            if (++_nodes > MaxNodes) throw new Stop($"Stopped after {MaxNodes:N0} parts: there's more, but too much to show.", truncated: true);
            if ((_nodes & 255) == 0)
            {
                cancel.ThrowIfCancellationRequested();
                if (_clock.Elapsed > budget) throw new Stop($"Stopped after {budget.TotalSeconds:0} seconds: there's more, but it's taking too long.", truncated: true);
            }
            parent.Children.Add(node);
            return node;
        }

        public void Exec(List<Template.Stmt> body, TemplateNode parent, Scope scope)
        {
            foreach (var s in body)
            {
                switch (s)
                {
                    case Template.Field f: Field(f, parent, scope); break;
                    case Template.Struct st: Struct(st, parent, scope); break;
                    case Template.Repeat r: Repeat(r, parent, scope); break;
                    case Template.If i:
                        if (Truthy(Eval(i.Condition, scope))) Exec(i.Then, parent, scope);
                        else if (i.Else != null) Exec(i.Else, parent, scope);
                        break;
                    case Template.Endian e: _big = e.Big; break;
                    case Template.Seek sk:
                        var to = Long(Eval(sk.To, scope), sk.Line);
                        if (to < 0 || to > data.Length) throw new Stop($"line {sk.Line}: can't seek to {Hex(to)}: the file is {Hex(data.Length)} long.");
                        _pos = to;
                        break;
                    case Template.Align a:
                        var n = Long(Eval(a.To, scope), a.Line);
                        if (n <= 0) throw new Stop($"line {a.Line}: align needs a size of 1 or more.");
                        var pad = Math.Min((n - _pos % n) % n, Math.Max(0, Limit - _pos));
                        if (pad > 0)
                        {
                            Add(parent, new TemplateNode { Name = "(padding)", Type = $"bytes[{pad}]", Offset = _pos, Length = pad, Value = Preview(_pos, pad), Parent = parent, IsField = true });
                            _pos += pad;
                        }
                        break;
                }
            }
        }

        private void Need(long bytes, string name, int line)
        {
            if (bytes < 0) throw new Stop($"line {line}: {name} can't be {bytes} long.");
            if (_pos + bytes > Limit)
            {
                var where = Limit == data.Length ? "the end of the file" : "the end of what holds it";
                throw new Stop($"line {line}: {name} needs {Plural(bytes, "byte")} at {Hex(_pos)}, but {where} is {Plural(Math.Max(0, Limit - _pos), "byte")} away.");
            }
        }

        private void Field(Template.Field f, TemplateNode parent, Scope scope)
        {
            var unit = Template.Types[f.Type];
            long? count = f.Count is null ? null : Long(Eval(f.Count, scope), f.Line);
            if (count < 0) throw new Stop($"line {f.Line}: {f.Name} can't have {count} items.");
            var start = _pos;
            object value;
            string shown;
            string type = count is { } c0 ? $"{f.Type}[{c0}]" : f.Type;
            List<TemplateNode>? items = null;
            switch (f.Type)
            {
                case "bytes":
                {
                    var n = count ?? 1;
                    Need(n, f.Name, f.Line);
                    value = n;
                    shown = Preview(_pos, n);
                    _pos += n;
                    break;
                }
                case "ascii" or "fourcc":
                {
                    var n = f.Type == "fourcc" ? 4 : count ?? 1;
                    Need(n, f.Name, f.Line);
                    var text = Text(_pos, n, Encoding.Latin1).TrimEnd('\0');
                    value = text;
                    shown = Quote(text, n > 256);
                    _pos += n;
                    break;
                }
                case "utf16":
                {
                    var n = (count ?? 1) * 2;
                    Need(n, f.Name, f.Line);
                    var text = Text(_pos, n, _big ? Encoding.BigEndianUnicode : Encoding.Unicode).TrimEnd('\0');
                    value = text;
                    shown = Quote(text, n > 512);
                    _pos += n;
                    break;
                }
                case "zstring":
                {
                    var max = Math.Min(count ?? 65536, Limit - _pos);
                    var buffer = new byte[(int)Math.Max(0, max)];
                    var got = data.Read(_pos, buffer);
                    var nul = Array.IndexOf(buffer, (byte)0, 0, got);
                    var n = nul >= 0 ? nul + 1 : got;
                    if (n == 0) Need(1, f.Name, f.Line);
                    var text = Encoding.Latin1.GetString(buffer, 0, nul >= 0 ? nul : got);
                    value = text;
                    shown = Quote(text, false);
                    type = "zstring";
                    _pos += n;
                    break;
                }
                default:
                {
                    // Numbers, one or an array.
                    var n = count ?? 1;
                    Need(n * unit, f.Name, f.Line);
                    if (count is null)
                    {
                        (value, shown) = Number(f.Type, _pos, f.Names);
                        _pos += unit;
                    }
                    else
                    {
                        value = n;
                        var parts = new List<string>();
                        if (n <= MaxArrayChildren) items = [];
                        for (long i = 0; i < n; i++)
                        {
                            if (items is null && i >= 8) break;
                            var (_, s) = Number(f.Type, _pos + i * unit, f.Names);
                            if (i < 8) parts.Add(Plain(s));
                            items?.Add(new TemplateNode { Name = $"[{i}]", Type = f.Type, Offset = _pos + i * unit, Length = unit, Value = s });
                        }
                        shown = string.Join(", ", parts) + (n > 8 ? ", …" : "");
                        _pos += n * unit;
                    }
                    break;
                }
            }
            var node = Add(parent, new TemplateNode { Name = f.Name, Type = type, Offset = start, Length = _pos - start, Value = shown, Parent = parent, IsField = true });
            if (items != null)
                foreach (var item in items)
                    Add(node, new TemplateNode { Name = item.Name, Type = item.Type, Offset = item.Offset, Length = item.Length, Value = item.Value, Parent = node });
            scope.Values[f.Name] = value;
            scope.Shown[f.Name] = shown;
        }

        private void Struct(Template.Struct st, TemplateNode parent, Scope scope)
        {
            var start = _pos;
            var node = Add(parent, new TemplateNode { Name = st.Name, Type = "struct", Offset = start, Parent = parent });
            var inner = new Scope(scope);
            scope.Values[st.Name] = inner;
            long? size = st.Size is null ? null : Long(Eval(st.Size, scope), st.Line);
            if (size is { } sz)
            {
                Need(sz, st.Name, st.Line);
                _limits.Push(start + sz);
            }
            var finished = false;
            try
            {
                Exec(st.Body, node, inner);
                finished = true;
            }
            finally
            {
                if (size is { } s2)
                {
                    _limits.Pop();
                    // Close the struct at exactly its size: the rest is shown as such, and what follows starts after it.
                    if (finished && _pos < start + s2)
                        Add(node, new TemplateNode { Name = "(rest)", Type = $"bytes[{start + s2 - _pos}]", Offset = _pos, Length = start + s2 - _pos, Value = Preview(_pos, start + s2 - _pos), Parent = node, IsField = true });
                    if (finished) _pos = start + s2;
                }
                Extent(node, start);
                Labelled(node, st.Label, inner);
            }
        }

        private void Repeat(Template.Repeat r, TemplateNode parent, Scope scope)
        {
            var start = _pos;
            var group = Add(parent, new TemplateNode { Name = r.Name, Type = "repeat", Offset = start, Parent = parent });
            long? count = r.Count is null ? null : Long(Eval(r.Count, scope), r.Line);
            try
            {
                for (long i = 0; ; i++)
                {
                    if (count is { } c ? i >= c : _pos >= Limit) break;
                    if (r.While != null && !Truthy(Eval(r.While, scope))) break;
                    var before = _pos;
                    var element = Add(group, new TemplateNode { Name = $"{r.Name}[{i}]", Type = r.Name, Offset = before, Parent = group });
                    var inner = new Scope(scope);
                    scope.Values[r.Name] = inner;
                    try
                    {
                        Exec(r.Body, element, inner);
                    }
                    finally
                    {
                        Extent(element, before);
                        Labelled(element, r.Label, inner);
                    }
                    if (_pos == before && count is null) break; // nothing read: going round again would never end
                }
            }
            finally
            {
                Extent(group, start);
                group.Value = $"{group.Children.Count:N0} item{(group.Children.Count == 1 ? "" : "s")}";
            }
        }

        /// <summary>A struct or repeat covers what its parts cover.</summary>
        private static void Extent(TemplateNode node, long start)
        {
            if (node.Children.Count == 0)
            {
                node.Offset = start;
                node.Length = 0;
                return;
            }
            node.Offset = node.Children.Min(c => c.Offset);
            node.Length = node.Children.Max(c => c.End) - node.Offset;
        }

        private static void Labelled(TemplateNode node, string? label, Scope scope)
        {
            if (label is null || !scope.Shown.TryGetValue(label, out var shown)) return;
            node.Value = Plain(shown);
        }

        /// <summary>A shown value without its hex or quotes: "IHDR", "PCM", "1024".</summary>
        private static string Plain(string shown)
        {
            if (shown.Length >= 2 && shown[0] == '"' && shown[^1] == '"') return shown[1..^1];
            var open = shown.IndexOf(" (", StringComparison.Ordinal);
            if (open > 0 && shown.EndsWith(')'))
            {
                var inside = shown[(open + 2)..^1];
                return inside.StartsWith("0x", StringComparison.Ordinal) ? shown[..open] : inside;
            }
            return shown;
        }

        private (object, string) Number(string type, long at, Dictionary<long, string>? names)
        {
            var size = Template.Types[type];
            Span<byte> b = stackalloc byte[8];
            b.Clear();
            data.Read(at, b[..size]);
            if (type == "f32")
            {
                var v = _big ? BinaryPrimitives.ReadSingleBigEndian(b) : BinaryPrimitives.ReadSingleLittleEndian(b);
                return ((double)v, v.ToString("G9", CultureInfo.InvariantCulture));
            }
            if (type == "f64")
            {
                var v = _big ? BinaryPrimitives.ReadDoubleBigEndian(b) : BinaryPrimitives.ReadDoubleLittleEndian(b);
                return (v, v.ToString("G17", CultureInfo.InvariantCulture));
            }
            ulong u = 0;
            for (var i = 0; i < size; i++) u |= (ulong)b[_big ? size - 1 - i : i] << (8 * i);
            long value;
            string text;
            if (type[0] == 'i')
            {
                var shift = 64 - size * 8;
                value = (long)(u << shift) >> shift;
                text = value.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                value = (long)u;
                text = u.ToString(CultureInfo.InvariantCulture);
                if (u >= 10) text += $" (0x{u.ToString("X", CultureInfo.InvariantCulture)})";
            }
            if (names != null && names.TryGetValue(value, out var name))
                text = $"{value.ToString(CultureInfo.InvariantCulture)} ({name})";
            return (value, text);
        }

        private string Text(long at, long n, Encoding encoding)
        {
            var buffer = new byte[(int)Math.Min(n, 4096)];
            var got = data.Read(at, buffer);
            return encoding.GetString(buffer, 0, got - got % (encoding == Encoding.Latin1 ? 1 : 2));
        }

        private static string Quote(string text, bool more)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in text.Length > 200 ? text[..200] : text)
                sb.Append(c switch { '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", < ' ' => "·", _ => c.ToString() });
            if (more || text.Length > 200) sb.Append('…');
            return sb.Append('"').ToString();
        }

        private string Preview(long at, long n)
        {
            if (n == 0) return "";
            var buffer = new byte[(int)Math.Min(n, 16)];
            var got = data.Read(at, buffer);
            return ByteFormats.ToHex(buffer.AsSpan(0, got)) + (n > 16 ? " …" : "");
        }

        // Expressions

        private object Eval(Template.Expr e, Scope scope)
        {
            switch (e)
            {
                case Template.Literal l: return l.Value;
                case Template.Ref r:
                    if (r.Path.Length == 1)
                    {
                        switch (r.Path[0])
                        {
                            case "pos": return _pos;
                            case "len": return data.Length;
                            case "remaining": return Math.Max(0, Limit - _pos);
                            case "eof": return _pos >= Limit ? 1L : 0L;
                        }
                    }
                    var v = scope.Find(r.Path[0]) ?? throw new Stop($"line {r.Line}: there's no field called {r.Path[0]} before this.");
                    for (var i = 1; i < r.Path.Length; i++)
                    {
                        if (v is not Scope s || !s.Values.TryGetValue(r.Path[i], out var next))
                            throw new Stop($"line {r.Line}: {string.Join('.', r.Path[..i])} has no field called {r.Path[i]}.");
                        v = next;
                    }
                    if (v is Scope) throw new Stop($"line {r.Line}: {string.Join('.', r.Path)} is a struct, not a value; name one of its fields.");
                    return v;
                case Template.Unary u:
                    var operand = Eval(u.Operand, scope);
                    return u.Op == "!" ? (Truthy(operand) ? 0L : 1L)
                        : operand is double d ? -d : -Long(operand, u.Line);
                case Template.Binary b:
                    if (b.Op == "&&") return Truthy(Eval(b.Left, scope)) && Truthy(Eval(b.Right, scope)) ? 1L : 0L;
                    if (b.Op == "||") return Truthy(Eval(b.Left, scope)) || Truthy(Eval(b.Right, scope)) ? 1L : 0L;
                    return Apply(b, Eval(b.Left, scope), Eval(b.Right, scope));
                case Template.Call c:
                    return Call(c, scope);
            }
            throw new Stop($"line {e.Line}: can't work this out.");
        }

        private static object Apply(Template.Binary b, object l, object r)
        {
            if (b.Op is "==" or "!=")
            {
                bool equal = (l, r) switch
                {
                    (string x, string y) => x == y,
                    (string, _) or (_, string) => false,
                    (double or long, double or long) when l is double || r is double => Convert.ToDouble(l, CultureInfo.InvariantCulture) == Convert.ToDouble(r, CultureInfo.InvariantCulture),
                    _ => (long)l == (long)r,
                };
                return equal == (b.Op == "==") ? 1L : 0L;
            }
            if (l is string || r is string) throw new Stop($"line {b.Line}: '{b.Op}' needs numbers, not text.");
            if (l is double || r is double)
            {
                double x = Convert.ToDouble(l, CultureInfo.InvariantCulture), y = Convert.ToDouble(r, CultureInfo.InvariantCulture);
                return b.Op switch
                {
                    "+" => x + y, "-" => x - y, "*" => x * y, "/" => x / y, "%" => x % y,
                    "<" => x < y ? 1L : 0L, ">" => x > y ? 1L : 0L, "<=" => x <= y ? 1L : 0L, ">=" => x >= y ? 1L : 0L,
                    _ => throw new Stop($"line {b.Line}: '{b.Op}' needs whole numbers."),
                };
            }
            long p = (long)l, q = (long)r;
            if (b.Op is "/" or "%" && q == 0) throw new Stop($"line {b.Line}: division by zero.");
            return b.Op switch
            {
                "+" => p + q, "-" => p - q, "*" => p * q, "/" => p / q, "%" => p % q,
                "<" => p < q ? 1L : 0L, ">" => p > q ? 1L : 0L, "<=" => p <= q ? 1L : 0L, ">=" => p >= q ? 1L : 0L,
                "&" => p & q, "|" => p | q, "^" => p ^ q,
                "<<" => q is >= 0 and < 64 ? p << (int)q : 0L, ">>" => q is >= 0 and < 64 ? p >> (int)q : 0L,
                _ => throw new Stop($"line {b.Line}: unknown operator '{b.Op}'."),
            };
        }

        private object Call(Template.Call c, Scope scope)
        {
            var args = c.Args.Select(a => Eval(a, scope)).ToList();
            var f = c.Function;
            if (f == "asciiat")
            {
                if (args.Count != 2) throw new Stop($"line {c.Line}: asciiat takes an offset and a length.");
                var at = Long(args[0], c.Line);
                var n = Math.Clamp(Long(args[1], c.Line), 0, 4096);
                return at < 0 ? "" : Text(at, n, Encoding.Latin1).TrimEnd('\0');
            }
            if (f is "min" or "max")
            {
                if (args.Count != 2) throw new Stop($"line {c.Line}: {f} takes two numbers.");
                long x = Long(args[0], c.Line), y = Long(args[1], c.Line);
                return f == "min" ? Math.Min(x, y) : Math.Max(x, y);
            }
            // u8at(offset) ... i64at(offset): the number there, or -1 past the end.
            if (f.EndsWith("at", StringComparison.Ordinal) && Template.Types.TryGetValue(f[..^2], out var size) && f[0] is 'u' or 'i')
            {
                if (args.Count != 1) throw new Stop($"line {c.Line}: {f} takes one offset.");
                var at = Long(args[0], c.Line);
                if (at < 0 || at + size > data.Length) return -1L;
                return Number(f[..^2], at, null).Item1;
            }
            throw new Stop($"line {c.Line}: there's no function called {f}. There are u8at to u64at, i8at to i64at, asciiat, min and max.");
        }

        private static bool Truthy(object v) => v switch
        {
            long l => l != 0,
            double d => d != 0,
            string s => s.Length > 0,
            _ => true,
        };

        private static long Long(object v, int line) => v switch
        {
            long l => l,
            double d when Math.Abs(d) < 9e18 => (long)d,
            _ => throw new Stop($"line {line}: expected a number but found {(v is string s ? $"\"{s}\"" : v)}."),
        };

        private static string Hex(long v) => "0x" + v.ToString("X", CultureInfo.InvariantCulture);
        private static string Plural(long n, string what) => $"{n:N0} {what}{(n == 1 ? "" : "s")}";
    }
}

/// <summary>
/// The templates to choose from: the built-in ones, and any <c>.hext</c> files in the user's templates folder. Picks
/// one for a file by its magic bytes first, then by its extension.
/// </summary>
public sealed class TemplateLibrary
{
    public List<Template> Templates { get; } = [];

    /// <summary>Templates that couldn't be read, and why.</summary>
    public List<string> Problems { get; } = [];

    public static TemplateLibrary Load(string? userFolder)
    {
        var lib = new TemplateLibrary();
        var asm = typeof(TemplateLibrary).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("Templates.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            lib.Add(reader.ReadToEnd(), name["Templates.".Length..]);
        }
        if (userFolder != null && Directory.Exists(userFolder))
            foreach (var file in Directory.EnumerateFiles(userFolder, "*.hext").Order(StringComparer.Ordinal))
            {
                try
                {
                    lib.Add(File.ReadAllText(file), file);
                }
                catch (IOException e)
                {
                    lib.Problems.Add($"{Path.GetFileName(file)}: {e.Message}");
                }
            }
        return lib;
    }

    private void Add(string text, string source)
    {
        try
        {
            var t = Template.Parse(text, source);
            // A template of the user's with the same name as a built-in one takes its place.
            Templates.RemoveAll(x => x.Name == t.Name);
            Templates.Add(t);
        }
        catch (TemplateException e)
        {
            Problems.Add($"{Path.GetFileName(source)}, {e.Message}");
        }
    }

    /// <summary>The template for a file: the first whose magic bytes match, else the first for its extension.</summary>
    public Template? Pick(IBytes data, string? fileName) =>
        Templates.FirstOrDefault(t => t.Magic.Count > 0 && t.MatchesMagic(data))
        ?? Templates.FirstOrDefault(t => t.Magic.Count == 0 && t.MatchesExtension(fileName))
        ?? Templates.FirstOrDefault(t => t.MatchesExtension(fileName));
}
