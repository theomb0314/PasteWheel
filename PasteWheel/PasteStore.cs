using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace PasteWheel;

public enum PasteKind { Folder, Hex, Url, Code, Text, Char, Image }

/// One node in the wheel: a folder (becomes a sub-wheel) or a paste entry backed
/// by a file. The folder tree on disk *is* the model — there is no database.
public sealed class PasteNode
{
    public required string Label { get; init; }
    public required string Path { get; init; }
    public bool IsFolder { get; init; }
    public List<PasteNode> Children { get; } = new();

    /// Text that gets pasted (text entries). Images use Path instead.
    public string Content { get; init; } = "";

    public PasteKind Kind { get; init; } = PasteKind.Folder;

    /// Parsed colour for Hex nodes; null otherwise.
    public Color? Swatch { get; init; }

    /// Optional per-folder accent colour / icon glyph (from _folder.json).
    public Color? Accent { get; init; }
    public string? Icon { get; init; }

    /// Short value shown under the reticle as a live preview.
    public string Preview => Kind switch
    {
        PasteKind.Folder => $"{Children.Count} item{(Children.Count == 1 ? "" : "s")}",
        PasteKind.Image => System.IO.Path.GetFileName(Path),
        _ => Content.Length > 60 ? Content[..57].Replace("\r", " ").Replace("\n", " ") + "…"
                                 : Content.Replace("\r", " ").Replace("\n", "↵"),
    };
}

/// Loads (and reloads) the paste tree from the root folder. Applies the optional
/// conventions: a leading "NN_" orders slices (and folders sharing a number merge
/// into one wedge), file contents are inspected to recognise hex/url/image, and an
/// optional _folder.json sets a folder's accent colour and icon.
public sealed class PasteStore
{
    public string RootPath { get; }
    public PasteNode Root { get; private set; }

    private static readonly Regex OrderPrefix = new(@"^\s*\d{1,3}[_\.\)\s-]+", RegexOptions.Compiled);
    private static readonly Regex PrefixNum = new(@"^\s*(\d{1,3})", RegexOptions.Compiled);
    private static readonly Regex HexColor = new(@"^#?([0-9A-Fa-f]{3}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.Compiled);
    private static readonly string[] TextExtensions = { ".txt", ".md", ".url", ".css", ".sql", ".json", ".csv", "" };
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    public PasteStore(string rootPath)
    {
        RootPath = rootPath;
        Root = LoadFolder(rootPath, isRoot: true);
    }

    public void Reload() => Root = LoadFolder(RootPath, isRoot: true);

    // Reads one folder into a node, merging child folders that share a number prefix.
    private PasteNode LoadFolder(string dir, bool isRoot = false)
    {
        var (accent, icon) = ReadFolderMeta(dir);
        var node = new PasteNode
        {
            Label = isRoot ? "Home" : CleanLabel(System.IO.Path.GetFileName(dir)),
            Path = dir,
            IsFolder = true,
            Kind = PasteKind.Folder,
            Accent = accent,
            Icon = icon,
        };

        if (!Directory.Exists(dir)) return node;

        var subDirs = Directory.EnumerateDirectories(dir)
            .Where(d => !System.IO.Path.GetFileName(d).StartsWith('.'))
            .OrderBy(SortKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Group folders by their numeric prefix; folders without one each stand alone.
        var groups = new List<List<string>>();
        var byNum = new Dictionary<int, List<string>>();
        foreach (var sub in subDirs)
        {
            int? num = ParsePrefixNum(System.IO.Path.GetFileName(sub));
            if (num is int n && byNum.TryGetValue(n, out var g)) g.Add(sub);
            else { var fresh = new List<string> { sub }; groups.Add(fresh); if (num is int n2) byNum[n2] = fresh; }
        }

        foreach (var group in groups)
        {
            if (group.Count == 1) node.Children.Add(LoadFolder(group[0]));
            else node.Children.Add(MergeFolders(group));
        }

        var files = Directory.EnumerateFiles(dir)
            .Where(IsPasteFile)
            .OrderBy(SortKey, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files) node.Children.Add(LoadFile(file));

        return node;
    }

    // Combines several same-numbered folders into a single virtual wedge.
    private PasteNode MergeFolders(List<string> dirs)
    {
        var members = dirs.Select(d => LoadFolder(d)).ToList();
        var merged = new PasteNode
        {
            Label = string.Join(" / ", members.Select(m => m.Label).Distinct()),
            Path = dirs[0],
            IsFolder = true,
            Kind = PasteKind.Folder,
            Accent = members.FirstOrDefault(m => m.Accent != null)?.Accent,
            Icon = members.FirstOrDefault(m => m.Icon != null)?.Icon,
        };
        foreach (var m in members) merged.Children.AddRange(m.Children);
        return merged;
    }

    private static bool IsPasteFile(string file)
    {
        var name = System.IO.Path.GetFileName(file);
        if (name.StartsWith('.') || name.Equals("_folder.json", StringComparison.OrdinalIgnoreCase)) return false;
        var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
        return TextExtensions.Contains(ext) || ImageExtensions.Contains(ext);
    }

    private static PasteNode LoadFile(string file)
    {
        var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
        var label = CleanLabel(System.IO.Path.GetFileNameWithoutExtension(file));

        // Image files paste as bitmaps; we keep only the path (no text content).
        if (ImageExtensions.Contains(ext))
            return new PasteNode { Label = label, Path = file, IsFolder = false, Kind = PasteKind.Image };

        string content;
        try { content = File.ReadAllText(file).Trim('﻿', '\r', '\n', ' '); }
        catch { content = ""; }

        var kind = Detect(content);
        return new PasteNode
        {
            Label = label,
            Path = file,
            IsFolder = false,
            Content = content,
            Kind = kind,
            Swatch = kind == PasteKind.Hex ? ParseHex(content) : null,
        };
    }

    // Reads an optional _folder.json for a folder's accent colour and icon glyph.
    private static (Color? accent, string? icon) ReadFolderMeta(string dir)
    {
        var path = System.IO.Path.Combine(dir, "_folder.json");
        if (!File.Exists(path)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Color? accent = doc.RootElement.TryGetProperty("accent", out var a) ? ParseHex(a.GetString() ?? "") : null;
            string? icon = doc.RootElement.TryGetProperty("icon", out var i) ? i.GetString() : null;
            return (accent, icon);
        }
        catch { return (null, null); }
    }

    public static PasteKind Detect(string content)
    {
        if (HexColor.IsMatch(content)) return PasteKind.Hex;
        if (content.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            content.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return PasteKind.Url;
        if (content.Contains('\n')) return PasteKind.Code;
        if (content.Length is > 0 and <= 3 && !content.Any(char.IsLetterOrDigit)) return PasteKind.Char;
        return PasteKind.Text;
    }

    public static Color? ParseHex(string raw)
    {
        var h = raw.Trim().TrimStart('#');
        try
        {
            if (h.Length == 3) return Color.FromRgb(Dup(h[0]), Dup(h[1]), Dup(h[2]));
            if (h.Length == 6) return Color.FromRgb(Hx(h, 0), Hx(h, 2), Hx(h, 4));
            if (h.Length == 8) return Color.FromArgb(Hx(h, 6), Hx(h, 0), Hx(h, 2), Hx(h, 4)); // RRGGBBAA
        }
        catch { /* fall through */ }
        return null;

        static byte Hx(string s, int i) => Convert.ToByte(s.Substring(i, 2), 16);
        static byte Dup(char c) => Convert.ToByte($"{c}{c}", 16);
    }

    private static int? ParsePrefixNum(string name)
    {
        var m = PrefixNum.Match(name);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    private static string SortKey(string path) => System.IO.Path.GetFileName(path);

    private static string CleanLabel(string name) => OrderPrefix.Replace(name, "").Trim();
}
