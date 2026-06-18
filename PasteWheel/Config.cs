using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PasteWheel;

/// <summary>
/// User settings, persisted as config.json next to the paste folders so the
/// whole tool is one portable directory.
/// </summary>
public sealed class Config
{
    /// <summary>Hotkey in "Ctrl+Alt+W" form (modifiers + one key).</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+W";

    /// <summary>When true, the value is delivered into the focused app automatically.</summary>
    public bool AutoPaste { get; set; } = true;

    /// <summary>
    /// How the value is delivered: "type" injects it as keystrokes (most reliable,
    /// works in WinUI/UWP apps), "paste" sends Ctrl+V (preserves clipboard formats).
    /// Either way the value is also placed on the clipboard.
    /// </summary>
    public string PasteMode { get; set; } = "type";

    /// <summary>Delay before sending Ctrl+V, letting the clipboard settle (ms).</summary>
    public int PasteDelayMs { get; set; } = 60;

    /// <summary>Wheel size: "small", "medium", "large", or a number = outer diameter in px.</summary>
    public string Size { get; set; } = "medium";

    /// <summary>Overall widget opacity 0.2–1.0 (colour swatches and images stay opaque).</summary>
    public double Opacity { get; set; } = 0.96;

    /// <summary>Optional override for the paste-folder root. Empty = default.</summary>
    public string RootPath { get; set; } = "";

    [JsonIgnore]
    public string ConfigDir { get; private set; } = "";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep "Ctrl+Alt+Space" readable
    };

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "PasteWheel");

    [JsonIgnore]
    public string ResolvedRoot =>
        string.IsNullOrWhiteSpace(RootPath) ? Path.Combine(ConfigDir, "Pastes") : RootPath;

    public static Config Load()
    {
        var dir = DefaultRoot;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");

        Config cfg;
        if (File.Exists(path))
        {
            try { cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(path)) ?? new Config(); }
            catch { cfg = new Config(); }
        }
        else
        {
            cfg = new Config();
        }

        cfg.ConfigDir = dir;
        cfg.Save();
        return cfg;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(Path.Combine(ConfigDir, "config.json"), JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* non-fatal */ }
    }
}
