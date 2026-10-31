# Writing templates

A template describes a file format, and AVAHexEdit lays it over a file's bytes: each field gets a name, a value and a
colour in the hex view, and the Template tab shows them as a tree. Templates are plain text files ending `.hext`.
Put yours in the templates folder (**Template › Open the templates folder**, or **Add a template file…**), then
**Template › Reload templates**. One with the same name as a built-in template takes its place.

The built-in templates are in [`src/AVAHexEdit.Core/Templates`](../src/AVAHexEdit.Core/Templates) and make good
examples.

## A first template

```
name "PNG image"
extensions "png" "apng"
magic "89 50 4E 47 0D 0A 1A 0A"
endian big

bytes[8] signature
repeat chunk label type {
  u32 length
  fourcc type
  if type == "IHDR" {
    struct header[length] {
      u32 width
      u32 height
      u8 bitDepth
      u8 colourType { 0: "greyscale", 2: "truecolour", 6: "truecolour and alpha" }
    }
  } else {
    bytes[length] data
  }
  u32 crc
}
```

## The header

| Line | What it does |
| --- | --- |
| `name "PNG image"` | The name in the template list. |
| `extensions "png" "apng"` | The file extensions it is for. |
| `magic "89 50 4E 47"` | Bytes at the start that identify the format, in hex; `??` matches any byte. Add `at 8` for bytes further in. More than one `magic` line means any of them. |
| `endian big` | The byte order for numbers (`little` is the default). |

With **Pick by the file's type** chosen, a template whose magic bytes match comes first, then one for the file's
extension.

## Fields

A field is a type and a name, with a count in brackets for more than one.

| Type | Bytes | Value |
| --- | --- | --- |
| `u8` `u16` `u24` `u32` `u64` | 1 to 8 | Unsigned whole number |
| `i8` `i16` `i24` `i32` `i64` | 1 to 8 | Signed whole number |
| `f32` `f64` | 4, 8 | Floating point |
| `bytes[n]` | n | The bytes, shown in hex |
| `ascii[n]` | n | Text, one byte a character |
| `utf16[n]` | 2n | Text in UTF-16 |
| `fourcc` | 4 | Four characters, as in IFF and RIFF chunk ids |
| `zstring` | to the 0 | Text up to and including a zero byte |

`u16[count] points` reads `count` numbers. A number can be given names, shown beside it:
`u16 format { 1: "PCM", 3: "IEEE float" }`.

A field's value can be used in anything after it: `bytes[length] data`, `if type == "IHDR"`. A struct's fields are
reached with a dot: `fileHeader.pixelOffset`.

## Statements

| Statement | What it does |
| --- | --- |
| `struct name { ... }` | A group of fields. |
| `struct name[size] { ... }` | A group exactly `size` bytes long: what it doesn't read is shown as `(rest)`, and what follows starts after it. Inside it, `remaining` and `repeat` stop at its end. |
| `repeat name { ... }` | Repeats to the end of the file (or of the struct it is in). |
| `repeat name[count] { ... }` | Repeats `count` times. |
| `repeat name while condition { ... }` | Repeats while the condition holds, checked before each one. |
| `... label field { ... }` | Shows that field's value beside each struct or repeat in the tree: `repeat chunk label type`. |
| `if condition { ... } else if ... { ... } else { ... }` | Reads some fields or others. |
| `endian big` / `endian little` | Changes the byte order from here on. |
| `seek offset` | Goes to an offset in the file. |
| `align n` | Skips to the next multiple of `n` bytes (from the start of the file), shown as `(padding)`. |

## Expressions

Numbers (`42`, `0x2A`), text in quotes, field names, and:

- `+ - * / %`, `<< >>`, `& | ^`, comparisons `== != < > <= >=`, and `&& || !`. `&`, `|` and `^` come before the
  comparisons, so `flags & 0x80 != 0` means `(flags & 0x80) != 0`.
- `pos` (where the next field starts), `len` (the file's length), `remaining` (bytes left in what holds it) and `eof`.
- `u8at(offset)` to `u64at(offset)` and `i8at` to `i64at`: the number at an offset, without making a field of it (-1
  past the end). `repeat file while u32at(pos) == 0x04034B50 { ... }` reads ZIP entries while there are more.
- `asciiat(offset, n)`, `min(a, b)`, `max(a, b)`.

`#` and `//` start comments.

## When it goes wrong

A mistake in a template is reported with its line in the Template tab and the template is left out of the list until
it's fixed. A field that runs past the end of the file (or of the struct holding it) stops the template there, saying
which and where; everything read before it still shows. Templates stop after 200,000 parts or five seconds, so a huge
file can't hang the window.
