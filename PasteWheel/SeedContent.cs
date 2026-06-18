using System.IO;

namespace PasteWheel;

/// On first run (empty root) writes a small tutorial set so a new user can see how
/// the wheel works. Everything here is just files/folders they can edit in Explorer.
internal static class SeedContent
{
    public static void EnsureSeeded(string root)
    {
        Directory.CreateDirectory(root);
        bool empty = !Directory.EnumerateFileSystemEntries(root)
            .Any(p => !Path.GetFileName(p).Equals("config.json", StringComparison.OrdinalIgnoreCase));
        if (!empty) return;

        // A root note explaining the conventions.
        File_(root, "Read Me First",
            "This text is one paste entry — pick it and it types here.\n" +
            "Add entries by dropping .txt files into these folders.\n" +
            "Prefix names with 01_, 02_ to set the order.");

        // Colours: demonstrates hex swatches + a per-folder accent and icon.
        Folder(root, "01_Colors", f =>
        {
            FolderMeta(f, accent: "#FF5A1F", icon: "🎨");
            File_(f, "Accent", "#FF5A1F");
            File_(f, "Success", "#4AC99B");
            File_(f, "Error", "#DA1414");
            File_(f, "Ink", "#191C30");
        });

        // Symbols: short non-alphanumeric entries.
        Folder(root, "02_Symbols", f =>
        {
            FolderMeta(f, icon: "✶");
            File_(f, "Degree", "°");
            File_(f, "Check", "✓");
            File_(f, "Arrow", "→");
            File_(f, "Bullet", "•");
        });

        // Snippets: longer / code-style entries.
        Folder(root, "03_Snippets", f =>
        {
            FolderMeta(f, icon: "{ }");
            File_(f, "RGBA accent", "rgba(255, 90, 31, 1)");
            File_(f, "ISO date", "yyyy-MM-dd");
            File_(f, "Lorem", "Lorem ipsum dolor sit amet.");
        });
    }

    private static void Folder(string parent, string name, Action<string> build)
    {
        var dir = Path.Combine(parent, name);
        Directory.CreateDirectory(dir);
        build(dir);
    }

    private static void File_(string dir, string label, string content)
    {
        var safe = string.Concat(label.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        File.WriteAllText(Path.Combine(dir, safe + ".txt"), content);
    }

    // Writes a folder's optional accent colour / icon glyph.
    private static void FolderMeta(string dir, string? accent = null, string? icon = null)
    {
        var parts = new List<string>();
        if (accent != null) parts.Add($"\"accent\": \"{accent}\"");
        if (icon != null) parts.Add($"\"icon\": \"{icon}\"");
        File.WriteAllText(Path.Combine(dir, "_folder.json"), "{ " + string.Join(", ", parts) + " }");
    }
}
