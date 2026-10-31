using System.Globalization;
using System.Text;

namespace AVAHexEdit.Core;

/// <summary>A mistake in a template, with the line it is on.</summary>
public sealed class TemplateException(int line, string message) : Exception($"line {line}: {message}")
{
    public int Line { get; } = line;
}

/// <summary>
/// A structure template: a short description of a file format, which <see cref="TemplateRunner"/> lays over a file's
/// bytes as named fields. Written in a small language of its own (see <c>docs/templates.md</c>):
/// <code>
/// name "PNG image"
/// extensions "png"
/// magic "89 50 4E 47 0D 0A 1A 0A"
/// endian big
///
/// bytes[8] signature
/// repeat chunk label type {
///   u32 length
///   fourcc type
///   bytes[length] data
///   u32 crc
/// }
/// </code>
/// </summary>
public sealed class Template
{
    public string Name { get; set; } = "Untitled template";
    public List<string> Extensions { get; } = [];

    /// <summary>Byte patterns that identify the format (any one is enough), each at an offset, with ?? for any byte.</summary>
    public List<(long Offset, Pattern Bytes)> Magic { get; } = [];

    public bool BigEndian { get; set; }
    public List<Stmt> Body { get; } = [];

    /// <summary>Where it came from: "built in", or the file it was read from.</summary>
    public string Source { get; init; } = "built in";

    /// <summary>Whether the bytes at the start of <paramref name="data"/> are this format's magic.</summary>
    public bool MatchesMagic(IBytes data)
    {
        foreach (var (offset, p) in Magic)
        {
            var buffer = new byte[p.Length];
            if (data.Read(offset, buffer) == p.Length && Finder.Matches(buffer, p)) return true;
        }
        return false;
    }

    public bool MatchesExtension(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").TrimStart('.');
        return ext.Length > 0 && Extensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
    }

    public override string ToString() => Name;

    // The language: statements and expressions.

    public abstract record Stmt(int Line);

    /// <summary><c>u32 size</c>, <c>bytes[size] data</c>, <c>u8 kind { 1: "PCM" }</c>.</summary>
    public sealed record Field(int Line, string Type, Expr? Count, string Name, Dictionary<long, string>? Names) : Stmt(Line);

    /// <summary><c>struct header { ... }</c>, or <c>struct data[size] { ... }</c> for one exactly that many bytes long.</summary>
    public sealed record Struct(int Line, string Name, Expr? Size, string? Label, List<Stmt> Body) : Stmt(Line);

    /// <summary><c>repeat chunk { ... }</c> to the end, <c>repeat point[n] { ... }</c>, or <c>repeat entry while cond { ... }</c>.</summary>
    public sealed record Repeat(int Line, string Name, Expr? Count, Expr? While, string? Label, List<Stmt> Body) : Stmt(Line);

    public sealed record If(int Line, Expr Condition, List<Stmt> Then, List<Stmt>? Else) : Stmt(Line);

    public sealed record Endian(int Line, bool Big) : Stmt(Line);

    public sealed record Seek(int Line, Expr To) : Stmt(Line);

    public sealed record Align(int Line, Expr To) : Stmt(Line);

    public abstract record Expr(int Line);

    public sealed record Literal(int Line, object Value) : Expr(Line);

    /// <summary>A field's value (<c>size</c>, <c>header.width</c>) or a built-in (<c>pos</c>, <c>len</c>, <c>remaining</c>, <c>eof</c>).</summary>
    public sealed record Ref(int Line, string[] Path) : Expr(Line);

    public sealed record Unary(int Line, string Op, Expr Operand) : Expr(Line);

    public sealed record Binary(int Line, string Op, Expr Left, Expr Right) : Expr(Line);

    /// <summary><c>u32at(pos)</c> and the like: the bytes at an offset, without making a field of them.</summary>
    public sealed record Call(int Line, string Function, List<Expr> Args) : Expr(Line);

    /// <summary>The field types, and how many bytes one of each takes (0: worked out as it is read).</summary>
    public static readonly Dictionary<string, int> Types = new()
    {
        ["u8"] = 1, ["u16"] = 2, ["u24"] = 3, ["u32"] = 4, ["u64"] = 8,
        ["i8"] = 1, ["i16"] = 2, ["i24"] = 3, ["i32"] = 4, ["i64"] = 8,
        ["f32"] = 4, ["f64"] = 8,
        ["bytes"] = 1, ["ascii"] = 1, ["utf16"] = 2, ["fourcc"] = 4, ["zstring"] = 0,
    };

    /// <summary>Words that can't name a field: the statements, and the built-in values.</summary>
    private static readonly HashSet<string> Keywords =
        ["endian", "struct", "repeat", "if", "else", "seek", "align", "while", "pos", "len", "remaining", "eof"];

    // Parsing

    /// <summary>Reads a template. Throws <see cref="TemplateException"/> for anything it can't make sense of.</summary>
    public static Template Parse(string text, string source = "built in")
    {
        var p = new Parser(Lexer.Tokens(text));
        var t = new Template { Source = source };
        p.Header(t);
        t.Body.AddRange(p.Statements(untilBrace: false));
        return t;
    }

    private enum Kind { Ident, Number, String, Symbol, End }

    private readonly record struct Token(Kind Kind, string Text, long Number, int Line);

    private static class Lexer
    {
        private static readonly string[] Symbols = ["==", "!=", "<=", ">=", "&&", "||", "<<", ">>", "{", "}", "[", "]", "(", ")", ",", ":", ".", "+", "-", "*", "/", "%", "<", ">", "!", "&", "|", "^"];

        public static List<Token> Tokens(string text)
        {
            var tokens = new List<Token>();
            var line = 1;
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (c == '\n') { line++; i++; continue; }
                if (char.IsWhiteSpace(c) || c == ';') { i++; continue; }
                if (c == '#' || (c == '/' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }
                if (char.IsAsciiDigit(c))
                {
                    var start = i;
                    if (c == '0' && i + 1 < text.Length && (text[i + 1] is 'x' or 'X'))
                    {
                        i += 2;
                        while (i < text.Length && (char.IsAsciiHexDigit(text[i]) || text[i] == '_')) i++;
                        var digits = text[(start + 2)..i].Replace("_", "", StringComparison.Ordinal);
                        if (!ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hv)) throw new TemplateException(line, $"\"{text[start..i]}\" isn't a number.");
                        tokens.Add(new Token(Kind.Number, text[start..i], (long)hv, line));
                    }
                    else
                    {
                        while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '_')) i++;
                        if (!long.TryParse(text[start..i].Replace("_", "", StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out var v)) throw new TemplateException(line, $"\"{text[start..i]}\" is too big.");
                        tokens.Add(new Token(Kind.Number, text[start..i], v, line));
                    }
                    if (i < text.Length && (char.IsAsciiLetter(text[i]) || text[i] == '_')) throw new TemplateException(line, $"\"{text[start..(i + 1)]}\" isn't a number or a name.");
                    continue;
                }
                if (char.IsAsciiLetter(c) || c == '_')
                {
                    var start = i;
                    while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    tokens.Add(new Token(Kind.Ident, text[start..i], 0, line));
                    continue;
                }
                if (c == '"')
                {
                    var sb = new StringBuilder();
                    i++;
                    while (true)
                    {
                        if (i >= text.Length || text[i] == '\n') throw new TemplateException(line, "A string is missing its closing \".");
                        if (text[i] == '"') { i++; break; }
                        if (text[i] == '\\' && i + 1 < text.Length) { sb.Append(text[i + 1] switch { 'n' => '\n', 't' => '\t', '0' => '\0', var x => x }); i += 2; continue; }
                        sb.Append(text[i++]);
                    }
                    tokens.Add(new Token(Kind.String, sb.ToString(), 0, line));
                    continue;
                }
                var sym = Symbols.FirstOrDefault(s => string.CompareOrdinal(text, i, s, 0, s.Length) == 0)
                          ?? throw new TemplateException(line, $"'{c}' doesn't belong here.");
                tokens.Add(new Token(Kind.Symbol, sym, 0, line));
                i += sym.Length;
            }
            tokens.Add(new Token(Kind.End, "", 0, line));
            return tokens;
        }
    }

    private sealed class Parser(List<Token> tokens)
    {
        private int _i;

        private Token Peek => tokens[_i];
        private Token Next() => tokens[_i++];
        private bool IsSymbol(string s) => Peek.Kind == Kind.Symbol && Peek.Text == s;
        private bool IsWord(string s) => Peek.Kind == Kind.Ident && Peek.Text == s;

        private void Expect(string symbol)
        {
            if (!IsSymbol(symbol)) throw new TemplateException(Peek.Line, $"Expected '{symbol}' but found {Describe(Peek)}.");
            _i++;
        }

        private static string Describe(Token t) => t.Kind switch
        {
            Kind.End => "the end of the template",
            Kind.String => $"\"{t.Text}\"",
            _ => $"'{t.Text}'",
        };

        private string Identifier(string what)
        {
            var t = Next();
            if (t.Kind != Kind.Ident) throw new TemplateException(t.Line, $"Expected {what} but found {Describe(t)}.");
            if (Keywords.Contains(t.Text) || Types.ContainsKey(t.Text)) throw new TemplateException(t.Line, $"'{t.Text}' is a word the language uses; call the {what} something else.");
            return t.Text;
        }

        private string String(string what)
        {
            var t = Next();
            if (t.Kind != Kind.String) throw new TemplateException(t.Line, $"Expected {what} in quotes but found {Describe(t)}.");
            return t.Text;
        }

        public void Header(Template t)
        {
            while (true)
            {
                if (IsWord("name"))
                {
                    _i++;
                    t.Name = String("the template's name");
                }
                else if (IsWord("extensions"))
                {
                    _i++;
                    while (Peek.Kind == Kind.String) t.Extensions.Add(Next().Text.TrimStart('.'));
                }
                else if (IsWord("magic"))
                {
                    var line = Next().Line;
                    var text = String("the magic bytes");
                    long at = 0;
                    if (IsWord("at"))
                    {
                        _i++;
                        var n = Next();
                        if (n.Kind != Kind.Number) throw new TemplateException(n.Line, "Expected the offset of the magic bytes.");
                        at = n.Number;
                    }
                    try
                    {
                        var (bytes, wild) = ByteFormats.ParseHex(text, allowWild: true);
                        t.Magic.Add((at, new Pattern(bytes, wild.Any(w => w) ? wild : null)));
                    }
                    catch (FormatException e)
                    {
                        throw new TemplateException(line, $"The magic bytes aren't hex: {e.Message}");
                    }
                }
                else return;
            }
        }

        public List<Stmt> Statements(bool untilBrace)
        {
            var list = new List<Stmt>();
            while (true)
            {
                if (Peek.Kind == Kind.End)
                {
                    if (untilBrace) throw new TemplateException(Peek.Line, "A '{' is missing its closing '}'.");
                    return list;
                }
                if (IsSymbol("}"))
                {
                    if (!untilBrace) throw new TemplateException(Peek.Line, "There's a '}' with no '{' to close.");
                    _i++;
                    return list;
                }
                list.Add(Statement());
            }
        }

        private List<Stmt> Block()
        {
            Expect("{");
            return Statements(untilBrace: true);
        }

        private Stmt Statement()
        {
            var t = Peek;
            if (t.Kind != Kind.Ident) throw new TemplateException(t.Line, $"Expected a field or a statement but found {Describe(t)}.");
            switch (t.Text)
            {
                case "struct":
                {
                    _i++;
                    var name = Identifier("the struct's name");
                    Expr? size = null;
                    if (IsSymbol("[")) { _i++; size = Expression(); Expect("]"); }
                    var label = Label();
                    return new Struct(t.Line, name, size, label, Block());
                }
                case "repeat":
                {
                    _i++;
                    var name = Identifier("a name for what repeats");
                    Expr? count = null, whileCond = null;
                    if (IsSymbol("[")) { _i++; count = Expression(); Expect("]"); }
                    if (IsWord("while")) { _i++; whileCond = Expression(); }
                    var label = Label();
                    return new Repeat(t.Line, name, count, whileCond, label, Block());
                }
                case "if":
                {
                    _i++;
                    var cond = Expression();
                    var then = Block();
                    List<Stmt>? otherwise = null;
                    if (IsWord("else"))
                    {
                        _i++;
                        otherwise = IsWord("if") ? [Statement()] : Block();
                    }
                    return new If(t.Line, cond, then, otherwise);
                }
                case "endian":
                {
                    _i++;
                    var which = Next();
                    if (which.Text is not ("big" or "little")) throw new TemplateException(which.Line, "endian is either big or little.");
                    return new Endian(t.Line, which.Text == "big");
                }
                case "seek":
                    _i++;
                    return new Seek(t.Line, Expression());
                case "align":
                    _i++;
                    return new Align(t.Line, Expression());
                case "else":
                    throw new TemplateException(t.Line, "There's an else with no if before it.");
                case "name" or "extensions" or "magic":
                    throw new TemplateException(t.Line, $"{t.Text} goes at the top, before the fields.");
            }
            if (!Types.ContainsKey(t.Text))
                throw new TemplateException(t.Line, $"'{t.Text}' isn't a type. The types are {string.Join(", ", Types.Keys)}.");
            _i++;
            Expr? n = null;
            if (IsSymbol("[")) { _i++; n = Expression(); Expect("]"); }
            var fieldName = Identifier("the field's name");
            Dictionary<long, string>? names = null;
            if (IsSymbol("{"))
            {
                // u16 format { 1: "PCM", 3: "IEEE float" }
                _i++;
                names = [];
                while (!IsSymbol("}"))
                {
                    var negative = IsSymbol("-");
                    if (negative) _i++;
                    var k = Next();
                    if (k.Kind != Kind.Number) throw new TemplateException(k.Line, $"Expected a number to name but found {Describe(k)}.");
                    Expect(":");
                    names[negative ? -k.Number : k.Number] = String("the number's name");
                    if (IsSymbol(",")) _i++;
                }
                _i++;
            }
            return new Field(t.Line, t.Text, n, fieldName, names);
        }

        private string? Label()
        {
            if (!IsWord("label")) return null;
            _i++;
            return Identifier("the field to label it with");
        }

        // Expressions, loosest first: || && | ^ & (== !=) (< > <= >=) (<< >>) (+ -) (* / %) unary.
        private static readonly string[][] Levels =
        [
            ["||"], ["&&"], ["|"], ["^"], ["&"], ["==", "!="], ["<", ">", "<=", ">="], ["<<", ">>"], ["+", "-"], ["*", "/", "%"],
        ];

        public Expr Expression(int level = 0)
        {
            if (level == Levels.Length) return UnaryExpr();
            var left = Expression(level + 1);
            while (Peek.Kind == Kind.Symbol && Levels[level].Contains(Peek.Text))
            {
                var op = Next();
                left = new Binary(op.Line, op.Text, left, Expression(level + 1));
            }
            return left;
        }

        private Expr UnaryExpr()
        {
            if (IsSymbol("!") || IsSymbol("-"))
            {
                var op = Next();
                return new Unary(op.Line, op.Text, UnaryExpr());
            }
            return Primary();
        }

        private Expr Primary()
        {
            var t = Next();
            switch (t.Kind)
            {
                case Kind.Number: return new Literal(t.Line, t.Number);
                case Kind.String: return new Literal(t.Line, t.Text);
                case Kind.Symbol when t.Text == "(":
                    var inner = Expression();
                    Expect(")");
                    return inner;
                case Kind.Ident:
                    if (IsSymbol("("))
                    {
                        _i++;
                        var args = new List<Expr>();
                        while (!IsSymbol(")"))
                        {
                            args.Add(Expression());
                            if (IsSymbol(",")) _i++;
                            else if (!IsSymbol(")")) throw new TemplateException(Peek.Line, $"Expected ',' or ')' but found {Describe(Peek)}.");
                        }
                        _i++;
                        return new Call(t.Line, t.Text, args);
                    }
                    var path = new List<string> { t.Text };
                    while (IsSymbol("."))
                    {
                        _i++;
                        var part = Next();
                        if (part.Kind != Kind.Ident) throw new TemplateException(part.Line, $"Expected a field name after '.' but found {Describe(part)}.");
                        path.Add(part.Text);
                    }
                    return new Ref(t.Line, [.. path]);
                default:
                    throw new TemplateException(t.Line, $"Expected a value but found {Describe(t)}.");
            }
        }
    }
}
