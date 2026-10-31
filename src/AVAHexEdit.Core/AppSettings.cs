using System.Text.Json;
using System.Text.Json.Serialization;

namespace AVAHexEdit.Core;

/// <summary>What the app remembers between runs, kept as JSON in the user's application data folder.</summary>
public sealed class AppSettings
{
    public List<string> Recent { get; set; } = [];
    public int BytesPerRow { get; set; } = 16;
    public TextEncodingChoice Encoding { get; set; } = TextEncodingChoice.Ascii;
    public bool HexOffsets { get; set; } = true;
    public bool BigEndian { get; set; }
    public FindMode FindMode { get; set; } = FindMode.Hex;
    public bool MatchCase { get; set; } = true;
    public bool ShowPanel { get; set; } = true;

    /// <summary>"auto" to pick by file type, "none", or a template's name.</summary>
    public string Template { get; set; } = "auto";

    public const int MaxRecent = 12;

    /// <summary>Puts a file at the top of the recent list.</summary>
    public void Opened(string path)
    {
        Recent.RemoveAll(p => string.Equals(p, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        Recent.Insert(0, path);
        if (Recent.Count > MaxRecent) Recent.RemoveRange(MaxRecent, Recent.Count - MaxRecent);
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>The app's folder in the user's application data: settings.json, and templates/ for their own templates.</summary>
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "AVAHexEdit");

    public static string DefaultPath => Path.Combine(Folder, "settings.json");

    /// <summary>The saved settings, or the defaults when there are none or they can't be read.</summary>
    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new();
                s.BytesPerRow = Math.Clamp(s.BytesPerRow, 4, 64);
                return s;
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        return new();
    }

    /// <summary>Saves them; a failure is ignored (settings are a convenience).</summary>
    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
