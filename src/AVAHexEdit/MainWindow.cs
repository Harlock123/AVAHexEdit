using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AVAHexEdit.Core;

namespace AVAHexEdit;

/// <summary>
/// The editor window: the menu and tool bar, the hex view, the side panel (inspector, template and checksums), the
/// find bar and the status bar. Built in code; its controls are public so tests can drive them.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly string _settingsPath;
    private readonly string _templatesFolder;
    private readonly AppSettings _settings;
    private TemplateLibrary _library;
    private CancellationTokenSource? _templateRun;
    private bool _closing;

    public Document Doc { get; private set; } = Document.New();
    public HexPane Pane { get; } = new();
    public HexView Hex => Pane.View;

    // Tool bar
    public TextBox GotoBox { get; } = new() { Width = 130, PlaceholderText = "Go to offset", FontFamily = Look.Mono };
    public ComboBox RowChoice { get; } = new() { ItemsSource = new[] { 8, 16, 24, 32, 48, 64 }, Width = 76 };
    public ComboBox EncodingChoice { get; } = new() { ItemsSource = new[] { "ASCII", "Latin-1" }, Width = 100 };
    public ToggleButton HexOffsetsToggle { get; } = new() { Content = "Hex offsets" };
    public ToggleButton InsertToggle { get; } = new() { Content = "Overwrite", MinWidth = 86 };

    // Status bar
    public TextBlock Message { get; } = new() { Foreground = Look.Heading, TextTrimming = TextTrimming.CharacterEllipsis };
    public TextBlock PositionInfo { get; } = new() { Foreground = Look.Text, FontFamily = Look.Mono, FontSize = 12 };
    public TextBlock SelectionInfo { get; } = new() { Foreground = Look.Text, FontFamily = Look.Mono, FontSize = 12 };
    public TextBlock LengthInfo { get; } = new() { Foreground = Look.Heading, FontSize = 12 };
    public TextBlock FieldInfo { get; } = new() { Foreground = Look.Accent, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    public TextBlock ModeInfo { get; } = new() { Foreground = Look.Heading, FontSize = 12, FontWeight = FontWeight.SemiBold };

    // Side panel
    public TabControl Panel { get; } = new();
    public RadioButton LittleEndian { get; } = new() { Content = "Little-endian", GroupName = "endian" };
    public RadioButton BigEndian { get; } = new() { Content = "Big-endian", GroupName = "endian" };
    public Dictionary<string, TextBox> InspectorValues { get; } = [];
    public ComboBox TemplateChoice { get; } = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    public TextBlock TemplateStatus { get; } = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Look.Heading };
    public TreeView TemplateTree { get; } = new();
    public TemplateResult? TemplateResult { get; private set; }
    public RadioButton ChecksumWhole { get; } = new() { Content = "Whole file", GroupName = "sum", IsChecked = true };
    public RadioButton ChecksumSelection { get; } = new() { Content = "Selection", GroupName = "sum" };
    public Dictionary<ChecksumKind, TextBox> ChecksumValues { get; } = [];
    public TextBlock ChecksumStatus { get; } = new() { FontSize = 12, Foreground = Look.Heading, TextWrapping = TextWrapping.Wrap };

    // Find bar
    public Border FindBar { get; } = new();
    public ComboBox FindModeChoice { get; } = new() { Width = 128 };
    public TextBox FindBox { get; } = new() { Width = 220, PlaceholderText = "Find", FontFamily = Look.Mono };
    public TextBox ReplaceBox { get; } = new() { Width = 180, PlaceholderText = "Replace with", FontFamily = Look.Mono };
    public CheckBox MatchCase { get; } = new() { Content = "Match case" };
    public CheckBox FindBigEndian { get; } = new() { Content = "Big-endian" };
    public TextBlock FindStatus { get; } = new() { FontSize = 12, Foreground = Look.Heading, VerticalAlignment = VerticalAlignment.Center };

    private readonly MenuItem _recentMenu = new() { Header = "Open _recent" };

    private static readonly (string Label, FindMode Mode)[] FindModes =
    [
        ("Hex bytes", FindMode.Hex), ("Text (UTF-8)", FindMode.Text), ("Text (UTF-16)", FindMode.Utf16),
        ("Int16", FindMode.Int16), ("Int32", FindMode.Int32), ("Int64", FindMode.Int64), ("Float", FindMode.Float), ("Double", FindMode.Double),
    ];

    /// <summary>Asks a question (title, message, buttons) and returns the button pressed; tests answer it themselves.</summary>
    public Func<string, string, string[], Task<int>> Ask { get; set; }

    public MainWindow(string settingsPath, string templatesFolder, string? open = null)
    {
        _settingsPath = settingsPath;
        _templatesFolder = templatesFolder;
        _settings = AppSettings.Load(settingsPath);
        _library = TemplateLibrary.Load(templatesFolder);
        Ask = (title, message, buttons) => AskDialog.Show(this, title, message, buttons);

        Width = 1280;
        Height = 820;
        MinWidth = 760;
        MinHeight = 420;
        Background = Look.Window;
        FontFeatures = Look.Plain;
        Resources["TreeViewItemMinHeight"] = 24.0; // a compact tree: templates can have thousands of parts
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://AVAHexEdit/Assets/AVAHexEdit.png")));

        Hex.BytesPerRow = _settings.BytesPerRow;
        Hex.HexOffsets = _settings.HexOffsets;
        Hex.Encoding = _settings.Encoding;

        var side = BuildPanel();
        side.IsVisible = _settings.ShowPanel;
        var centre = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,400") };
        centre.Children.Add(new Border { Child = Pane, BorderBrush = Look.Border, BorderThickness = new Thickness(0, 1, 1, 0) });
        var splitter = new GridSplitter { Background = Look.Window, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(side, 2);
        centre.Children.Add(splitter);
        centre.Children.Add(side);
        splitter.IsVisible = side.IsVisible;
        side.PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            splitter.IsVisible = side.IsVisible;
            centre.ColumnDefinitions[2].Width = side.IsVisible ? new GridLength(400) : new GridLength(0);
        };
        if (!side.IsVisible) centre.ColumnDefinitions[2].Width = new GridLength(0);

        var root = new DockPanel();
        var menu = BuildMenu(side);
        DockPanel.SetDock(menu, Dock.Top);
        root.Children.Add(menu);
        var tools = BuildToolBar();
        DockPanel.SetDock(tools, Dock.Top);
        root.Children.Add(tools);
        var status = BuildStatusBar();
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);
        BuildFindBar();
        DockPanel.SetDock(FindBar, Dock.Bottom);
        root.Children.Add(FindBar);
        root.Children.Add(centre);
        Content = root;

        Hex.CaretMoved += OnCaretMoved;
        Hex.ModeChanged += UpdateMode;
        Hex.Problem += m => Note(m, error: true);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Closing += OnClosing;

        SetDocument(Document.New());
        if (open != null) OpenFile(open);
        else Note("Open a file (Ctrl+O), or start typing hex digits to make a new one.");
        Opened += (_, _) => Hex.Focus();
    }

    // Building the window

    private Menu BuildMenu(Control side)
    {
        MenuItem Item(string header, Action action, string? gesture = null)
        {
            var m = new MenuItem { Header = header };
            if (gesture != null) m.InputGesture = KeyGesture.Parse(gesture);
            m.Click += (_, _) => action();
            return m;
        }
        MenuItem Sub(string header, params Control[] items)
        {
            var m = new MenuItem { Header = header };
            foreach (var i in items) m.Items.Add(i);
            return m;
        }

        RebuildRecent();
        return new Menu
        {
            Background = Look.Bar,
            Items =
            {
                Sub("_File",
                    Item("_New", () => _ = New(), "Ctrl+N"),
                    Item("_Open…", () => _ = OpenWithPicker(), "Ctrl+O"),
                    _recentMenu,
                    new Separator(),
                    Item("_Save", () => _ = Save(), "Ctrl+S"),
                    Item("Save _as…", () => _ = SaveAs(), "Ctrl+Shift+S"),
                    new Separator(),
                    Item("_Compare with…", () => _ = CompareWithPicker()),
                    new Separator(),
                    Item("E_xit", Close)),
                Sub("_Edit",
                    Item("_Undo", Hex.Undo, "Ctrl+Z"),
                    Item("_Redo", Hex.Redo, "Ctrl+Y"),
                    new Separator(),
                    Item("Cu_t", () => _ = Hex.Cut(), "Ctrl+X"),
                    Item("_Copy", () => _ = Hex.Copy(Hex.TextColumn ? HexView.CopyFormat.Text : HexView.CopyFormat.Hex), "Ctrl+C"),
                    Sub("Copy _as",
                        Item("Hex bytes", () => _ = Hex.Copy(HexView.CopyFormat.Hex)),
                        Item("Text", () => _ = Hex.Copy(HexView.CopyFormat.Text)),
                        Item("C array", () => _ = Hex.Copy(HexView.CopyFormat.CArray)),
                        Item("Base64", () => _ = Hex.Copy(HexView.CopyFormat.Base64))),
                    Item("_Paste", () => _ = Hex.Paste(), "Ctrl+V"),
                    new Separator(),
                    Item("Select _all", Hex.SelectAll, "Ctrl+A"),
                    Item("_Insert or overwrite", () => Hex.InsertMode = !Hex.InsertMode, "Insert"),
                    Item("_Go to offset…", FocusGoto, "Ctrl+G")),
                Sub("_Search",
                    Item("_Find…", () => ShowFind(replace: false), "Ctrl+F"),
                    Item("Find _next", () => _ = FindNext(forward: true), "F3"),
                    Item("Find _previous", () => _ = FindNext(forward: false), "Shift+F3"),
                    Item("_Replace…", () => ShowFind(replace: true), "Ctrl+H")),
                Sub("_View",
                    Item("Side _panel", () => { side.IsVisible = !side.IsVisible; _settings.ShowPanel = side.IsVisible; SaveSettings(); }, "F9"),
                    Item("_Inspector", () => { side.IsVisible = true; Panel.SelectedIndex = 0; }),
                    Item("_Template", () => { side.IsVisible = true; Panel.SelectedIndex = 1; }),
                    Item("_Checksums", () => { side.IsVisible = true; Panel.SelectedIndex = 2; })),
                Sub("_Template",
                    Item("_Reload templates", ReloadTemplates),
                    Item("_Add a template file…", () => _ = AddTemplate()),
                    Item("_Open the templates folder", () => _ = OpenTemplatesFolder())),
            },
        };
    }

    private Control BuildToolBar()
    {
        Button Tool(string text, string tip, Action action)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 4) };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => action();
            return b;
        }
        RowChoice.SelectedItem = Hex.BytesPerRow;
        RowChoice.SelectionChanged += (_, _) =>
        {
            if (RowChoice.SelectedItem is not int n) return;
            Hex.BytesPerRow = n;
            _settings.BytesPerRow = n;
            SaveSettings();
        };
        ToolTip.SetTip(RowChoice, "Bytes to a row");
        EncodingChoice.SelectedIndex = Hex.Encoding == TextEncodingChoice.Latin1 ? 1 : 0;
        EncodingChoice.SelectionChanged += (_, _) =>
        {
            Hex.Encoding = EncodingChoice.SelectedIndex == 1 ? TextEncodingChoice.Latin1 : TextEncodingChoice.Ascii;
            _settings.Encoding = Hex.Encoding;
            SaveSettings();
        };
        ToolTip.SetTip(EncodingChoice, "How the text column shows bytes, and what typing there writes");
        HexOffsetsToggle.IsChecked = Hex.HexOffsets;
        HexOffsetsToggle.IsCheckedChanged += (_, _) =>
        {
            Hex.HexOffsets = HexOffsetsToggle.IsChecked == true;
            _settings.HexOffsets = Hex.HexOffsets;
            SaveSettings();
            OnCaretMoved();
        };
        ToolTip.SetTip(HexOffsetsToggle, "Offsets in hex, or in decimal");
        InsertToggle.IsCheckedChanged += (_, _) => Hex.InsertMode = InsertToggle.IsChecked == true;
        ToolTip.SetTip(InsertToggle, "Typing writes over the bytes, or inserts them (Insert)");
        GotoBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { GoTo(GotoBox.Text ?? ""); e.Handled = true; }
            else if (e.Key == Key.Escape) { Hex.Focus(); e.Handled = true; }
        };
        ToolTip.SetTip(GotoBox, "Go to an offset (Ctrl+G): 1F40 in hex, #8000 in decimal, 0x1F40, or +100 / -100 to move");

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(8, 6),
            Children =
            {
                Tool("Open", "Open a file (Ctrl+O)", () => _ = OpenWithPicker()),
                Tool("Save", "Save (Ctrl+S)", () => _ = Save()),
                Tool("Undo", "Undo (Ctrl+Z)", Hex.Undo),
                Tool("Redo", "Redo (Ctrl+Y)", Hex.Redo),
                Tool("Find", "Find and replace (Ctrl+F)", () => ShowFind(replace: false)),
                Tool("Compare", "Compare with another file", () => _ = CompareWithPicker()),
                new Border { Width = 10 },
                GotoBox,
                new TextBlock { Text = "Row", VerticalAlignment = VerticalAlignment.Center, Foreground = Look.Heading, Margin = new Thickness(8, 0, 0, 0) },
                RowChoice,
                EncodingChoice,
                HexOffsetsToggle,
                InsertToggle,
            },
        };
        return new Border { Background = Look.Bar, Child = bar, BorderBrush = Look.Border, BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    private Control BuildStatusBar()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto"),
            Margin = new Thickness(10, 4),
        };
        Control[] items = [Message, FieldInfo, PositionInfo, SelectionInfo, LengthInfo, ModeInfo];
        for (var i = 0; i < items.Length; i++)
        {
            Grid.SetColumn(items[i], i);
            if (i > 0) items[i].Margin = new Thickness(18, 0, 0, 0);
            if (items[i] is TextBlock t) t.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(items[i]);
        }
        FieldInfo.MaxWidth = 340;
        return new Border { Background = Look.Bar, Child = grid, BorderBrush = Look.Border, BorderThickness = new Thickness(0, 1, 0, 0) };
    }

    private void BuildFindBar()
    {
        FindModeChoice.ItemsSource = FindModes.Select(m => m.Label).ToList();
        FindModeChoice.SelectedIndex = Math.Max(0, Array.FindIndex(FindModes, m => m.Mode == _settings.FindMode));
        FindModeChoice.SelectionChanged += (_, _) =>
        {
            _settings.FindMode = CurrentFindMode;
            SaveSettings();
            UpdateFindOptions();
        };
        MatchCase.IsChecked = _settings.MatchCase;
        MatchCase.IsCheckedChanged += (_, _) => { _settings.MatchCase = MatchCase.IsChecked == true; SaveSettings(); };
        FindBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { _ = FindNext(forward: (e.KeyModifiers & KeyModifiers.Shift) == 0); e.Handled = true; }
        };
        ReplaceBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { _ = ReplaceOne(); e.Handled = true; }
        };
        Button B(string text, string tip, Action a)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 4) };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => a();
            return b;
        }
        var close = B("✕", "Close (Esc)", HideFind);
        var row1 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                FindModeChoice, FindBox,
                B("Previous", "Find the one before (Shift+F3)", () => _ = FindNext(forward: false)),
                B("Next", "Find the next one (F3, or Enter)", () => _ = FindNext(forward: true)),
                B("Count", "Count every match", () => _ = CountAll()),
                MatchCase, FindBigEndian,
            },
        };
        var row2 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                ReplaceBox,
                B("Replace", "Replace this match and find the next", () => _ = ReplaceOne()),
                B("Replace all", "Replace every match, as one step to undo", () => _ = ReplaceAll()),
                FindStatus,
            },
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(8, 6) };
        var rows = new StackPanel { Spacing = 6, Children = { row1, row2 } };
        grid.Children.Add(rows);
        Grid.SetColumn(close, 1);
        close.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(close);
        FindBar.Child = grid;
        FindBar.Background = Look.Bar;
        FindBar.BorderBrush = Look.Border;
        FindBar.BorderThickness = new Thickness(0, 1, 0, 0);
        FindBar.IsVisible = false;
        UpdateFindOptions();
    }

    private void UpdateFindOptions()
    {
        var mode = CurrentFindMode;
        MatchCase.IsVisible = mode is FindMode.Text or FindMode.Utf16;
        FindBigEndian.IsVisible = mode is not (FindMode.Hex or FindMode.Text or FindMode.Utf16);
        FindBox.PlaceholderText = mode switch
        {
            FindMode.Hex => "Hex bytes: 4D 5A ?? 00",
            FindMode.Text or FindMode.Utf16 => "Text (\\n, \\t, \\x1B work)",
            FindMode.Float or FindMode.Double => "A number: 3.14",
            _ => "A number: 1234 or 0x4D2",
        };
    }

    private Control BuildPanel()
    {
        // Inspector
        LittleEndian.IsChecked = !_settings.BigEndian;
        BigEndian.IsChecked = _settings.BigEndian;
        BigEndian.IsCheckedChanged += (_, _) =>
        {
            _settings.BigEndian = BigEndian.IsChecked == true;
            SaveSettings();
            UpdateInspector();
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 6, 0, 0) };
        var names = Inspector.Read(new byte[Inspector.Needs], false).Select(r => r.Name).ToList();
        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = name, Foreground = Look.Heading, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            var writable = Writing.CanWrite(name);
            var box = new TextBox
            {
                FontFamily = Look.Mono,
                FontSize = 11.5,
                IsReadOnly = !writable,
                MinHeight = 24,
                Padding = new Thickness(6, 2),
                Margin = new Thickness(0, 1),
                Background = writable ? Look.Sheet : Brushes.Transparent,
                BorderThickness = new Thickness(writable ? 1 : 0),
            };
            if (writable)
            {
                ToolTip.SetTip(box, $"Type a new {name} and press Enter to write it at the caret");
                box.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter) { WriteValue(name, box.Text ?? ""); e.Handled = true; }
                    else if (e.Key == Key.Escape) { UpdateInspector(); Hex.Focus(); e.Handled = true; }
                };
            }
            Grid.SetRow(label, i);
            Grid.SetRow(box, i);
            Grid.SetColumn(box, 1);
            grid.Children.Add(label);
            grid.Children.Add(box);
            InspectorValues[name] = box;
        }
        var inspector = new DockPanel { Margin = new Thickness(10) };
        var endian = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { LittleEndian, BigEndian } };
        DockPanel.SetDock(endian, Dock.Top);
        inspector.Children.Add(endian);
        inspector.Children.Add(new ScrollViewer { Content = grid });

        // Template
        TemplateChoice.SelectionChanged += (_, _) =>
        {
            if (TemplateChoice.SelectedItem is not ChoiceItem c) return;
            _settings.Template = c.Key;
            SaveSettings();
            RunTemplate();
        };
        TemplateTree.ItemTemplate = new FuncTreeDataTemplate<NodeItem>((n, _) => n.View(), n => n.Children);
        TemplateTree.SelectionChanged += (_, _) =>
        {
            if (TemplateTree.SelectedItem is NodeItem { Node: var node })
            {
                Hex.Outline = node.Length > 0 ? new ByteRange(node.Offset, node.Length) : null;
                Hex.Select(node.Offset, node.Length);
            }
        };
        var template = new DockPanel { Margin = new Thickness(10) };
        var top = new StackPanel { Spacing = 6, Children = { TemplateChoice, TemplateStatus } };
        DockPanel.SetDock(top, Dock.Top);
        template.Children.Add(top);
        TemplateTree.Margin = new Thickness(0, 6, 0, 0);
        template.Children.Add(TemplateTree);
        RefreshTemplateChoices();

        // Checksums
        var sums = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        var kinds = Enum.GetValues<ChecksumKind>();
        for (var i = 0; i < kinds.Length; i++)
        {
            var kind = kinds[i];
            sums.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = Checksums.Name(kind), Foreground = Look.Heading, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            var box = new TextBox { FontFamily = Look.Mono, FontSize = 11.5, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2), Padding = new Thickness(6, 3) };
            var copy = new Button { Content = "Copy", FontSize = 11, Padding = new Thickness(8, 2), Margin = new Thickness(6, 2, 0, 2), VerticalAlignment = VerticalAlignment.Center };
            copy.Click += async (_, _) =>
            {
                if (!string.IsNullOrEmpty(box.Text) && Clipboard is { } c) await c.SetTextAsync(box.Text);
            };
            Grid.SetRow(label, i);
            Grid.SetRow(box, i);
            Grid.SetColumn(box, 1);
            Grid.SetRow(copy, i);
            Grid.SetColumn(copy, 2);
            sums.Children.Add(label);
            sums.Children.Add(box);
            sums.Children.Add(copy);
            ChecksumValues[kind] = box;
        }
        var compute = new Button { Content = "Work them out", Classes = { "accent" } };
        compute.Click += (_, _) => _ = ComputeChecksums();
        var checksums = new StackPanel
        {
            Margin = new Thickness(10),
            Spacing = 6,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { ChecksumWhole, ChecksumSelection } },
                compute,
                ChecksumStatus,
                sums,
            },
        };

        Panel.ItemsSource = new[]
        {
            new TabItem { Header = "Inspector", Content = inspector, FontSize = 14 },
            new TabItem { Header = "Template", Content = template, FontSize = 14 },
            new TabItem { Header = "Checksums", Content = new ScrollViewer { Content = checksums }, FontSize = 14 },
        };
        return new Border { Background = Look.Window, Child = Panel, BorderBrush = Look.Border, BorderThickness = new Thickness(1, 1, 0, 0) };
    }

    /// <summary>A template choice: the key saved in the settings, and what the list shows.</summary>
    public sealed record ChoiceItem(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    private void RefreshTemplateChoices()
    {
        var items = new List<ChoiceItem> { new("auto", "Pick by the file's type"), new("none", "No template") };
        items.AddRange(_library.Templates.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => new ChoiceItem(t.Name, t.Name)));
        TemplateChoice.ItemsSource = items;
        TemplateChoice.SelectedItem = items.FirstOrDefault(i => i.Key == _settings.Template) ?? items[0];
    }

    /// <summary>A template part in the tree, with its children made only when it is opened.</summary>
    public sealed class NodeItem(TemplateNode node)
    {
        public TemplateNode Node { get; } = node;

        /// <summary>Most parts the tree shows under one; the rest are summed up in a last line.</summary>
        public const int MaxShown = 1000;

        public IEnumerable<NodeItem> Children => field ??= Make();

        private List<NodeItem> Make()
        {
            var list = Node.Children.Take(MaxShown).Select(c => new NodeItem(c)).ToList();
            if (Node.Children.Count > MaxShown)
                list.Add(new NodeItem(new TemplateNode { Name = $"… {Node.Children.Count - MaxShown:N0} more", Offset = Node.Children[MaxShown].Offset, Length = 0, Parent = Node }));
            return list;
        }

        public Control View()
        {
            var swatch = new Border
            {
                Width = 9,
                Height = 9,
                CornerRadius = new CornerRadius(2),
                Background = Node.IsField ? Look.FieldSwatches[Node.Colour % Look.FieldSwatches.Length] : Brushes.Transparent,
                BorderBrush = Node.IsField ? null : Look.Faint,
                BorderThickness = new Thickness(Node.IsField ? 0 : 1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    swatch,
                    new TextBlock { Text = Node.Name, Foreground = Look.Text, FontSize = 12.5 },
                    new TextBlock { Text = Node.Value, Foreground = Node.IsField ? Look.Accent : Look.Heading, FontSize = 12.5, FontFamily = Node.IsField ? Look.Mono : FontFamily.Default, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            };
            ToolTip.SetTip(panel, $"{Node.Type}  at 0x{Node.Offset:X} ({Node.Offset:N0}), {ByteFormats.Size(Node.Length)}" + (Node.Value.Length > 0 ? $"\n{Node.Value}" : ""));
            return panel;
        }
    }

    // Documents

    private void SetDocument(Document doc)
    {
        var old = Doc;
        Doc.Changed -= OnDocumentChanged;
        Doc = doc;
        Doc.Changed += OnDocumentChanged;
        Hex.Document = doc;
        if (!ReferenceEquals(old, doc)) old.Dispose();
        foreach (var box in ChecksumValues.Values) box.Text = "";
        ChecksumStatus.Text = "";
        UpdateTitle();
        OnCaretMoved();
        UpdateMode();
        RunTemplate();
    }

    private void OnDocumentChanged()
    {
        UpdateTitle();
        OnCaretMoved();
        ScheduleTemplate();
    }

    private void UpdateTitle() => Title = $"{Doc.Name}{(Doc.IsModified ? " •" : "")} — AVAHexEdit";

    /// <summary>Opens a file in place of the one open (call <see cref="ConfirmDiscard"/> first). Returns false, with a note, if it can't.</summary>
    public bool OpenFile(string path)
    {
        try
        {
            var doc = Document.Open(path);
            SetDocument(doc);
            _settings.Opened(doc.Path!);
            SaveSettings();
            RebuildRecent();
            Note($"Opened {doc.Path} ({ByteFormats.Size(doc.Length)}).");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Note($"Couldn't open {path}: {e.Message}", error: true);
            _settings.Recent.Remove(path);
            SaveSettings();
            RebuildRecent();
            return false;
        }
    }

    private async Task New()
    {
        if (!await ConfirmDiscard()) return;
        SetDocument(Document.New());
        Note("A new, empty file: type hex digits to fill it.");
    }

    private async Task OpenWithPicker()
    {
        if (!await ConfirmDiscard()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open a file", AllowMultiple = false });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) OpenFile(path);
    }

    private void RebuildRecent()
    {
        _recentMenu.Items.Clear();
        foreach (var path in _settings.Recent)
        {
            var item = new MenuItem { Header = path };
            item.Click += async (_, _) =>
            {
                if (await ConfirmDiscard()) OpenFile(path);
            };
            _recentMenu.Items.Add(item);
        }
        _recentMenu.IsEnabled = _settings.Recent.Count > 0;
    }

    /// <summary>Whether it's all right to drop the open document: it has no changes, they were saved, or they can go.</summary>
    public async Task<bool> ConfirmDiscard()
    {
        if (!Doc.IsModified) return true;
        var answer = await Ask("Unsaved changes", $"Save the changes to {Doc.Name}?", ["Save", "Don't save", "Cancel"]);
        return answer switch
        {
            0 => await Save(),
            1 => true,
            _ => false,
        };
    }

    /// <summary>Saves to the file it came from, or asks where for a new one. Returns whether it was saved.</summary>
    public async Task<bool> Save() => Doc.Path is { } path ? await SaveTo(path) : await SaveAs();

    public async Task<bool> SaveAs()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save as",
            SuggestedFileName = Doc.Path is null ? "untitled.bin" : Path.GetFileName(Doc.Path),
        });
        return file?.TryGetLocalPath() is { } path && await SaveTo(path);
    }

    /// <summary>Saves to a file: written on another thread (the window stays responsive, but can't be edited meanwhile).</summary>
    public async Task<bool> SaveTo(string path)
    {
        Hex.ReadOnly = true;
        Note($"Saving {ByteFormats.Size(Doc.Length)} to {path}…");
        var doc = Doc;
        try
        {
            var written = await Task.Run(() => doc.WriteCopy(path));
            doc.FinishSave(written, path);
            _settings.Opened(doc.Path!);
            SaveSettings();
            RebuildRecent();
            Note($"Saved {doc.Path} ({ByteFormats.Size(doc.Length)}).");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Note($"Couldn't save to {path}: {e.Message}", error: true);
            return false;
        }
        finally
        {
            Hex.ReadOnly = false;
            UpdateTitle();
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing || !Doc.IsModified)
        {
            _templateRun?.Cancel();
            SaveSettings();
            return;
        }
        e.Cancel = true;
        if (!await ConfirmDiscard()) return;
        _closing = true;
        Close();
    }

    // The caret, inspector and status bar

    private void OnCaretMoved()
    {
        var at = Hex.Caret;
        PositionInfo.Text = Hex.HexOffsets ? $"Offset 0x{at:X} ({at:N0})" : $"Offset {at:N0} (0x{at:X})";
        SelectionInfo.Text = Hex.HasSelection ? $"Selected {Hex.Selection.Length:N0} (0x{Hex.Selection.Length:X})" : "";
        LengthInfo.Text = $"{ByteFormats.Size(Doc.Length)}" + (Doc.Length >= 1024 ? $" ({Doc.Length:N0} bytes)" : "");
        var field = TemplateResult?.FieldAt(Hex.InspectAt);
        FieldInfo.Text = field is null ? "" : $"{field.Path} = {field.Value}";
        UpdateInspector();
    }

    private void UpdateMode()
    {
        InsertToggle.IsChecked = Hex.InsertMode;
        InsertToggle.Content = Hex.InsertMode ? "Insert" : "Overwrite";
        ModeInfo.Text = Hex.InsertMode ? "INS" : "OVR";
    }

    private void UpdateInspector()
    {
        var bytes = Doc.ReadArray(Hex.InspectAt, Inspector.Needs);
        foreach (var r in Inspector.Read(bytes, BigEndian.IsChecked == true))
            if (InspectorValues.TryGetValue(r.Name, out var box) && !box.IsFocused)
                box.Text = r.Value;
    }

    /// <summary>Writes a value typed into the inspector at the caret, in the byte order chosen.</summary>
    public void WriteValue(string name, string text)
    {
        try
        {
            var bytes = Writing.Bytes(name, text, BigEndian.IsChecked == true);
            var at = Hex.InspectAt;
            Doc.Overwrite(at, bytes);
            Doc.EndMerge();
            Note($"Wrote {name} {text.Trim()} at 0x{at:X} ({ByteFormats.ToHex(bytes)}).");
            Hex.InvalidateVisual();
            if (InspectorValues.TryGetValue(name, out var box)) box.Text = Inspector.Read(Doc.ReadArray(at, Inspector.Needs), BigEndian.IsChecked == true).First(r => r.Name == name).Value;
            OnCaretMoved();
        }
        catch (FormatException e)
        {
            Note(e.Message, error: true);
        }
    }

    /// <summary>A note in the status bar: what happened, or what went wrong (in red).</summary>
    public void Note(string text, bool error = false)
    {
        Message.Text = text;
        Message.Foreground = error ? Look.Error : Look.Heading;
        ToolTip.SetTip(Message, text);
    }

    private void SaveSettings() => _settings.Save(_settingsPath);

    // Keys

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var command = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        switch (e.Key)
        {
            case Key.O when command: _ = OpenWithPicker(); break;
            case Key.N when command: _ = New(); break;
            case Key.S when command && shift: _ = SaveAs(); break;
            case Key.S when command: _ = Save(); break;
            case Key.F when command: ShowFind(replace: false); break;
            case Key.H when command: ShowFind(replace: true); break;
            case Key.G when command: FocusGoto(); break;
            case Key.F3: _ = FindNext(forward: !shift); break;
            case Key.F9:
                if (Panel.Parent is Control side) { side.IsVisible = !side.IsVisible; _settings.ShowPanel = side.IsVisible; SaveSettings(); }
                break;
            case Key.Escape when FindBar.IsVisible: HideFind(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void FocusGoto()
    {
        GotoBox.Focus();
        GotoBox.SelectAll();
    }

    /// <summary>Goes to an offset typed in the go-to box.</summary>
    public bool GoTo(string text)
    {
        if (!ByteFormats.TryParseOffset(text, Hex.Caret, Hex.HexOffsets, out var offset))
        {
            Note($"\"{text}\" isn't an offset. Type 1F40 for hex{(Hex.HexOffsets ? "" : " (or 0x1F40)")}, #8000 for decimal, or +100 / -100 to move.", error: true);
            return false;
        }
        if (offset > Doc.Length)
        {
            Note($"0x{offset:X} is past the end (the file is 0x{Doc.Length:X} long).", error: true);
            return false;
        }
        Hex.MoveTo(offset);
        Hex.Focus();
        Note($"At 0x{offset:X} ({offset:N0}).");
        return true;
    }

    // Find and replace

    private FindMode CurrentFindMode => FindModes[Math.Max(0, FindModeChoice.SelectedIndex)].Mode;

    private CancellationTokenSource? _search;

    public void ShowFind(bool replace)
    {
        FindBar.IsVisible = true;
        // Start from the selection, if it's short: its bytes in hex, or its text.
        if (Hex.HasSelection && Hex.Selection.Length <= 64 && string.IsNullOrEmpty(FindBox.Text))
        {
            var bytes = Doc.ReadArray(Hex.Selection.Start, (int)Hex.Selection.Length);
            if (Hex.TextColumn) { FindModeChoice.SelectedIndex = 1; FindBox.Text = ByteFormats.ToText(bytes, Hex.Encoding); }
            else { FindModeChoice.SelectedIndex = 0; FindBox.Text = ByteFormats.ToHex(bytes); }
        }
        var box = replace ? ReplaceBox : FindBox;
        Dispatcher.UIThread.Post(() => { box.Focus(); box.SelectAll(); });
    }

    public void HideFind()
    {
        FindBar.IsVisible = false;
        _search?.Cancel();
        Hex.Focus();
    }

    private Pattern? FindPattern()
    {
        try
        {
            return Pattern.Parse(FindBox.Text ?? "", CurrentFindMode, MatchCase.IsChecked == true, FindBigEndian.IsChecked == true);
        }
        catch (FormatException e)
        {
            FindStatus.Text = e.Message;
            FindStatus.Foreground = Look.Error;
            return null;
        }
    }

    private void FindNote(string text, bool error = false)
    {
        FindStatus.Text = text;
        FindStatus.Foreground = error ? Look.Error : Look.Heading;
    }

    /// <summary>Finds the next match after the caret (or the one before it), going round the end of the file if needed.</summary>
    public async Task<bool> FindNext(bool forward = true)
    {
        if (!FindBar.IsVisible) FindBar.IsVisible = true;
        if (FindPattern() is not { } p) return false;
        _search?.Cancel();
        var cancel = (_search = new CancellationTokenSource()).Token;
        var data = Doc.Snapshot();
        var from = forward ? (Hex.HasSelection ? Hex.Selection.Start + 1 : Hex.Caret) : Hex.InspectAt;
        FindNote("Searching…");
        try
        {
            var (at, wrapped) = await Task.Run(() =>
            {
                var found = Finder.Find(data, p, from, forward, cancel);
                if (found >= 0) return (found, false);
                return (Finder.Find(data, p, forward ? 0 : data.Length, forward, cancel), true);
            }, cancel);
            if (at < 0)
            {
                FindNote("Not found.", error: true);
                return false;
            }
            Hex.Select(at, p.Length);
            FindNote(wrapped ? $"Found at 0x{at:X}, going round from the {(forward ? "start" : "end")}." : $"Found at 0x{at:X}.");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            FindNote("The file changed while searching; try again.", error: true);
            return false;
        }
    }

    public async Task<int> CountAll()
    {
        if (FindPattern() is not { } p) return 0;
        var data = Doc.Snapshot();
        FindNote("Counting…");
        var all = await Task.Run(() => Finder.FindAll(data, p));
        FindNote(all.Count == 0 ? "Not found." : $"{all.Count:N0} match{(all.Count == 1 ? "" : "es")}{(all.Count >= 1_000_000 ? " or more" : "")}.", all.Count == 0);
        return all.Count;
    }

    private byte[]? ReplacementBytes()
    {
        try
        {
            var text = ReplaceBox.Text ?? "";
            var mode = CurrentFindMode;
            if (text.Length == 0) return [];
            return mode switch
            {
                FindMode.Hex => ByteFormats.ParseHex(text).Bytes,
                FindMode.Text or FindMode.Utf16 => Pattern.Parse(text, mode).Bytes,
                _ => Pattern.Number(text, mode, FindBigEndian.IsChecked == true),
            };
        }
        catch (FormatException e)
        {
            FindNote($"Replace with: {e.Message}", error: true);
            return null;
        }
    }

    /// <summary>Replaces the match that is selected (if it is one), then finds the next.</summary>
    public async Task ReplaceOne()
    {
        if (Hex.ReadOnly || FindPattern() is not { } p || ReplacementBytes() is not { } with) return;
        var s = Hex.Selection;
        if (Hex.HasSelection && s.Length == p.Length && Finder.Matches(Doc.ReadArray(s.Start, p.Length), p))
        {
            Doc.Replace(s.Start, s.Length, with);
            Doc.EndMerge();
            Hex.MoveTo(s.Start + with.Length);
        }
        await FindNext(forward: true);
    }

    /// <summary>Replaces every match, as one step to undo, and returns how many there were.</summary>
    public async Task<int> ReplaceAll()
    {
        if (Hex.ReadOnly || FindPattern() is not { } p || ReplacementBytes() is not { } with) return 0;
        var data = Doc.Snapshot();
        FindNote("Replacing…");
        var all = await Task.Run(() => Finder.FindAll(data, p));
        if (all.Count == 0)
        {
            FindNote("Not found.", error: true);
            return 0;
        }
        Doc.ReplaceRuns(all, p.Length, with);
        Hex.MoveTo(all[0]);
        FindNote($"Replaced {all.Count:N0}.");
        return all.Count;
    }

    // Templates

    private DispatcherTimer? _templateTimer;

    /// <summary>Runs the template again a moment after editing stops.</summary>
    private void ScheduleTemplate()
    {
        _templateTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            _templateTimer!.Stop();
            RunTemplate();
        });
        _templateTimer.Stop();
        _templateTimer.Start();
    }

    /// <summary>The template chosen, or picked for the file's type; null for none.</summary>
    public Template? ChosenTemplate => _settings.Template switch
    {
        "none" => null,
        "auto" => _library.Pick(Doc, Doc.Path),
        var name => _library.Templates.FirstOrDefault(t => t.Name == name),
    };

    /// <summary>Runs the template over the file on another thread; <see cref="TemplateRunning"/> is the run, for tests to wait on.</summary>
    public void RunTemplate()
    {
        _templateRun?.Cancel();
        var template = ChosenTemplate;
        if (template is null)
        {
            ShowTemplate(null);
            TemplateStatus.Text = _settings.Template == "auto"
                ? "No template for this kind of file." + Problems()
                : "No template." + Problems();
            TemplateRunning = Task.CompletedTask;
            return;
        }
        var cancel = (_templateRun = new CancellationTokenSource()).Token;
        var data = Doc.Snapshot();
        TemplateStatus.Text = $"Reading it as {template.Name}…";
        TemplateRunning = Run();
        return;

        async Task Run()
        {
            try
            {
                var result = await Task.Run(() => TemplateRunner.Run(template, data, cancel), cancel);
                if (cancel.IsCancellationRequested) return;
                ShowTemplate(result);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>The template run under way, if any (finished when there is none).</summary>
    public Task TemplateRunning { get; private set; } = Task.CompletedTask;

    private string Problems() => _library.Problems.Count == 0 ? "" : "\n\nTemplates that couldn't be read:\n" + string.Join("\n", _library.Problems);

    private void ShowTemplate(TemplateResult? result)
    {
        TemplateResult = result;
        if (result is null)
        {
            TemplateTree.ItemsSource = null;
            Hex.Tints = [];
            Hex.Outline = null;
            OnCaretMoved();
            return;
        }
        TemplateTree.ItemsSource = new NodeItem(result.Root).Children;
        Hex.Tints = result.Fields.Select(f => new Tint(f.Offset, f.Length, f.Colour)).ToList();
        Hex.Outline = null;
        var note = $"{result.Template.Name}: {result.Fields.Count:N0} field{(result.Fields.Count == 1 ? "" : "s")}.";
        if (result.Error != null) note += $"\n{(result.Truncated ? "" : "Stopped at ")}{result.Error}";
        TemplateStatus.Text = note + Problems();
        TemplateStatus.Foreground = result.Error != null && !result.Truncated ? Look.Error : Look.Heading;
        OnCaretMoved();
    }

    private void ReloadTemplates()
    {
        _library = TemplateLibrary.Load(_templatesFolder);
        RefreshTemplateChoices();
        RunTemplate();
        Note(_library.Problems.Count == 0 ? $"Loaded {_library.Templates.Count} templates." : $"Loaded {_library.Templates.Count} templates; {_library.Problems.Count} couldn't be read (see the Template tab).", _library.Problems.Count > 0);
    }

    private async Task AddTemplate()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add a template",
            FileTypeFilter = [new FilePickerFileType("Templates") { Patterns = ["*.hext"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        try
        {
            Core.Template.Parse(await File.ReadAllTextAsync(path), path);
            Directory.CreateDirectory(_templatesFolder);
            File.Copy(path, Path.Combine(_templatesFolder, Path.GetFileName(path)), overwrite: true);
            ReloadTemplates();
        }
        catch (TemplateException e)
        {
            Note($"{Path.GetFileName(path)}, {e.Message}", error: true);
        }
        catch (IOException e)
        {
            Note($"Couldn't add {path}: {e.Message}", error: true);
        }
    }

    private async Task OpenTemplatesFolder()
    {
        Directory.CreateDirectory(_templatesFolder);
        var folder = await StorageProvider.TryGetFolderFromPathAsync(_templatesFolder);
        if (folder is null || !await Launcher.LaunchFileAsync(folder))
            Note($"Your templates go in {_templatesFolder}.");
    }

    // Checksums

    public async Task ComputeChecksums()
    {
        var range = ChecksumSelection.IsChecked == true && Hex.HasSelection ? Hex.Selection : new ByteRange(0, Doc.Length);
        var what = range.Length == Doc.Length ? "the whole file" : $"{ByteFormats.Size(range.Length)} from 0x{range.Start:X}";
        ChecksumStatus.Text = $"Working out {what}…";
        var data = Doc.Snapshot();
        var progress = new Progress<double>(f => ChecksumStatus.Text = $"Working out {what}… {f:P0}");
        try
        {
            var sums = await Task.Run(() => Checksums.Compute(data, range, progress));
            foreach (var (kind, value) in sums) ChecksumValues[kind].Text = value;
            ChecksumStatus.Text = $"Of {what} ({range.Length:N0} bytes).";
        }
        catch (ObjectDisposedException)
        {
            ChecksumStatus.Text = "The file changed while working them out; try again.";
        }
    }

    // Comparing

    private async Task CompareWithPicker()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = $"Compare {Doc.Name} with", AllowMultiple = false });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) CompareWith(path);
    }

    /// <summary>
    /// Opens a window comparing the document (as it is now, edits and all) with another file. It stays in front of
    /// this one until it is closed, so the document can't change under it.
    /// </summary>
    public CompareWindow? CompareWith(string path)
    {
        try
        {
            var w = new CompareWindow(Document.View(Doc.Snapshot(), Doc.Name), Document.Open(path), Hex.BytesPerRow, Hex.HexOffsets, Hex.Encoding);
            _ = w.ShowDialog(this);
            return w;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Note($"Couldn't open {path}: {e.Message}", error: true);
            return null;
        }
    }
}
