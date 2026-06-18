using System.Windows.Forms;

namespace PasteWheel;

/// <summary>Parses a "Ctrl+Alt+Space" style string into RegisterHotKey arguments.</summary>
internal readonly record struct Hotkey(NativeMethods.Mod Modifiers, uint VirtualKey, string Display)
{
    public static Hotkey Parse(string text)
    {
        var mods = NativeMethods.Mod.NoRepeat;
        var keyToken = "";

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= NativeMethods.Mod.Control; break;
                case "alt": mods |= NativeMethods.Mod.Alt; break;
                case "shift": mods |= NativeMethods.Mod.Shift; break;
                case "win" or "super" or "meta": mods |= NativeMethods.Mod.Win; break;
                default: keyToken = raw; break;
            }
        }

        uint vk = ToVirtualKey(keyToken);
        return new Hotkey(mods, vk, text);
    }

    private static uint ToVirtualKey(string token)
    {
        if (string.IsNullOrEmpty(token)) return (uint)Keys.Space;

        // Single letters/digits map directly.
        if (token.Length == 1)
        {
            char c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
        }

        // Named keys: Space, Enter, F1..F12, etc.
        if (Enum.TryParse<Keys>(token, ignoreCase: true, out var key))
            return (uint)key;

        return (uint)Keys.Space;
    }
}
