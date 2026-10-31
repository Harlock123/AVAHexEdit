# AGENTS.md

Notes for anyone, human or AI agent, changing this code. `README.md` says what the app does; this says how the code
is put together and what keeps it working.

## What this is

AVAHexEdit is a hex editor in C# on .NET 10 with an Avalonia 12 window. It opens files of any size, edits them
(overwrite and insert, undo, the clipboard), reads the bytes at the caret every common way, lays structure templates
over files, finds and replaces, works out checksums and compares two files.

## Commands

```bash
dotnet build AVAHexEdit.slnx
dotnet test AVAHexEdit.slnx                 # headless; the window tests run on Avalonia's headless platform
dotnet run --project src/AVAHexEdit         # the app
AVAHEXEDIT_SHOTS=docs/images dotnet test AVAHexEdit.slnx --filter "FullyQualifiedName~Opening_a_png|FullyQualifiedName~Comparing"
                                            # redraws the README's screenshots
```

## Layout

- `src/AVAHexEdit.Core/`: everything but the window, with no Avalonia in it.
  - `ByteSource.cs`: `IBytes` (anything readable at an offset), `MemorySource`, and `FileSource`, which reads a file
    in 64 KB pages as they are needed and keeps the last few.
  - `Document.cs`: the file being edited, as a piece table: pieces of the original file and of a buffer that only
    grows. Each state is an immutable array of pieces, so undo keeps states, `Snapshot()` gives other threads a copy
    that never changes, and saving writes a new file beside the old one before it takes its place.
  - `Search.cs` (`Pattern`, `Finder`), `Inspector.cs` (`Inspector`, and `Writing` for values typed back),
    `Checksums.cs` (`Checksums`, `Comparison`), `ByteFormats.cs` (hex, C arrays, Base64, offsets), `AppSettings.cs`.
  - `Template.cs` (the template language: lexer, parser, statements), `TemplateRunner.cs` (lays a template over the
    bytes as a tree of `TemplateNode`s; `TemplateLibrary` loads the built-in ones and the user's), and `Templates/*.hext`
    (the built-in templates, embedded as resources). The language is documented in `docs/templates.md`.
- `src/AVAHexEdit/`: the Avalonia app, built in code rather than XAML. `HexView` draws and edits the bytes (only the
  rows on screen, read from the document as it draws); `HexPane` adds its scroll bar; `MainWindow` is the rest (its
  controls are public so tests can drive them); `CompareWindow`; `AskDialog`; `Look` has the colours.
- `tests/AVAHexEdit.Tests/`: xUnit v3 with Avalonia.Headless.XUnit. `Core/` tests the library; `WindowTests` drives
  the window with keys, the mouse and its public methods, and takes the screenshots.

## Rules that keep it working

- **Nothing in Core touches a window.** Anything that can be tested without one goes in Core, with tests.
- **Never read a whole file.** Read what is shown, searched or saved, a block at a time, through `IBytes`.
- **Work that takes time runs on a snapshot** (`Document.Snapshot()`) on another thread: search, templates,
  checksums, comparing and writing a save. Only the UI thread changes the document.
- **Edits go through `Document.Replace`** (or `ReplaceRuns`), so each is one undo step; `merge` joins typing into one.
- **Errors say what to do**, in the status bar, the find bar or the Template tab; never a bare exception.
- **Template mistakes carry their line**, and a template that runs past the end stops there, keeping what it read.
- **Warnings are errors** (`TreatWarningsAsErrors`), and nullable reference types are on.

## CI and releases

`.github/workflows/build.yml` builds and tests every push, then publishes self-contained single-file builds for
win-x64/x86/arm64, linux-x64/arm64/arm and osx-x64/arm64. A push to `main` refreshes the rolling `latest`
pre-release; a `v*` tag makes a proper release. New repos use `main`: the release step only runs there.
