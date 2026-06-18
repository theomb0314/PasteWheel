using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PasteWheel;

/// A tiny modal dialog that records the next Ctrl/Alt/Shift/Win + key combo and
/// returns it as a "Ctrl+Alt+W" style string (or null if cancelled).
internal sealed class HotkeyCaptureWindow : Window
{
    private string _result;
    private readonly TextBlock _display;
    private readonly Button _save;

    private HotkeyCaptureWindow(string current)
    {
        Title = "Set PasteWheel hotkey";
        Width = 380; Height = 190;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x20));

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "Press a shortcut. Needs at least one of Ctrl / Alt / Shift / Win.",
            Foreground = Brushes.White, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });
        _display = new TextBlock
        {
            Text = current, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x1F)),
            FontSize = 22, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 16),
        };
        panel.Children.Add(_display);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _save = new Button { Content = "Save", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsEnabled = !string.IsNullOrEmpty(current) };
        var cancel = new Button { Content = "Cancel", Width = 80 };
        _save.Click += (_, _) => DialogResult = true;
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(_save);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;

        _result = current;
        PreviewKeyDown += OnKey;
    }

    public static string? Capture(string current)
    {
        var w = new HotkeyCaptureWindow(current);
        return w.ShowDialog() == true ? w._result : null;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifier(key)) return; // wait for the non-modifier key

        string? token = KeyToken(key);
        if (token == null) { _display.Text = "Unsupported key"; _save.IsEnabled = false; return; }

        var mods = new List<string>();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods.Add("Ctrl");
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods.Add("Alt");
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods.Add("Shift");
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods.Add("Win");

        if (mods.Count == 0) { _display.Text = token + "  (add a modifier)"; _save.IsEnabled = false; return; }

        _result = string.Join("+", mods) + "+" + token;
        _display.Text = _result;
        _save.IsEnabled = true;
    }

    private static bool IsModifier(Key k) =>
        k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
          or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    // Maps a WPF key to a token Hotkey.Parse understands (letters, digits, F-keys, Space).
    private static string? KeyToken(Key k)
    {
        if (k is >= Key.A and <= Key.Z) return k.ToString();
        if (k is >= Key.D0 and <= Key.D9) return ((char)('0' + (k - Key.D0))).ToString();
        if (k is >= Key.NumPad0 and <= Key.NumPad9) return ((char)('0' + (k - Key.NumPad0))).ToString();
        if (k is >= Key.F1 and <= Key.F12) return k.ToString();
        if (k == Key.Space) return "Space";
        return null;
    }
}
