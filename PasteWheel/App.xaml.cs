using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace PasteWheel;

public partial class App : Application
{
    private const int HotkeyId = 0xC0DE;
    private const string RunKeyName = "PasteWheel";

    private Config _config = null!;
    private PasteStore _store = null!;
    private WheelWindow _wheel = null!;
    private WinForms.NotifyIcon _tray = null!;
    private HwndSource _msgSink = null!;
    private FileSystemWatcher? _watcher;

    private Hotkey _hotkey;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _config = Config.Load();
        var root = _config.ResolvedRoot;
        SeedContent.EnsureSeeded(root);

        _store = new PasteStore(root);
        _wheel = new WheelWindow(_store, _config) { OnPaste = HandlePaste };

        CreateMessageSink();
        CreateTray();                 // tray first, so failures can notify non-modally
        RegisterHotkeyFromConfig();
        WatchFolder(root);
    }

    // ---------- global hotkey ----------

    private void CreateMessageSink()
    {
        // A 0-size, never-shown window purely to receive WM_HOTKEY.
        var prms = new HwndSourceParameters("PasteWheelMsgSink")
        {
            Width = 0, Height = 0,
            WindowStyle = 0, // not visible
        };
        _msgSink = new HwndSource(prms);
        _msgSink.AddHook(WndProc);
    }

    private void RegisterHotkeyFromConfig()
    {
        _hotkey = Hotkey.Parse(_config.Hotkey);
        NativeMethods.UnregisterHotKey(_msgSink.Handle, HotkeyId);
        bool ok = NativeMethods.RegisterHotKey(_msgSink.Handle, HotkeyId,
            (uint)_hotkey.Modifiers, _hotkey.VirtualKey);
        if (!ok)
        {
            // Non-blocking notification so startup (and the tray) is never stalled.
            _tray.ShowBalloonTip(6000, "PasteWheel",
                $"Couldn't register the hotkey \"{_config.Hotkey}\". " +
                "Another app may use it — edit config.json to change it.",
                WinForms.ToolTipIcon.Warning);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            ToggleWheel();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ToggleWheel()
    {
        if (_wheel.IsVisible)
        {
            _wheel.Dismiss();
            return;
        }
        _wheel.ShowAt();
    }

    // ---------- paste ----------

    private void HandlePaste(PasteNode node)
    {
        // Images go on the clipboard as a bitmap and are always delivered with Ctrl+V.
        bool isImage = node.Kind == PasteKind.Image;
        if (isImage) SetClipboardImage(node.Path);
        else { try { Clipboard.SetText(node.Content); } catch { /* clipboard busy */ } }

        if (!_config.AutoPaste) return;

        // The wheel never took focus (no-activate window), so the user's app is still
        // focused. After a short settle, type the text or send Ctrl+V, per config.
        bool typeIt = !isImage && !string.Equals(_config.PasteMode, "paste", StringComparison.OrdinalIgnoreCase);
        string content = node.Content;
        int delay = isImage ? Math.Max(_config.PasteDelayMs, 140) : _config.PasteDelayMs;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (typeIt) NativeMethods.SendText(content);
            else NativeMethods.SendPaste();
        };
        timer.Start();
    }

    private static void SetClipboardImage(string path)
    {
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            Clipboard.SetImage(bmp);
        }
        catch { /* unreadable image; ignore */ }
    }

    // ---------- tray ----------

    private void CreateTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = BuildTrayIcon(),
            Visible = true,
            Text = $"PasteWheel  —  {_config.Hotkey}",
        };
        _tray.DoubleClick += (_, _) => ToggleWheel();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open wheel", null, (_, _) => ToggleWheel());
        menu.Items.Add("Open paste folder", null, (_, _) => OpenFolder());
        menu.Items.Add("Reload pastes", null, (_, _) => _store.Reload());
        menu.Items.Add("Change hotkey…", null, (_, _) => ChangeHotkey());
        menu.Items.Add(new WinForms.ToolStripSeparator());

        var autoPaste = new WinForms.ToolStripMenuItem("Auto-paste")
        { Checked = _config.AutoPaste, CheckOnClick = true };
        autoPaste.CheckedChanged += (_, _) => { _config.AutoPaste = autoPaste.Checked; _config.Save(); };
        menu.Items.Add(autoPaste);

        var startup = new WinForms.ToolStripMenuItem("Start with Windows")
        { Checked = IsRunAtStartup(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) => SetRunAtStartup(startup.Checked);
        menu.Items.Add(startup);

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
        _tray.ContextMenuStrip = menu;
    }

    private static Icon BuildTrayIcon()
    {
        // Draw a small wheel glyph at runtime so we ship no binary assets.
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            g.FillEllipse(new SolidBrush(ColorTranslator.FromHtml("#002059")), 1, 1, 30, 30);
            // four "slices" as accent arcs
            using var pen = new Pen(ColorTranslator.FromHtml("#FA6B2C"), 4);
            g.DrawArc(pen, 5, 5, 22, 22, -80, 70);
            using var pen2 = new Pen(ColorTranslator.FromHtml("#4AC99B"), 4);
            g.DrawArc(pen2, 5, 5, 22, 22, 100, 70);
            g.FillEllipse(new SolidBrush(ColorTranslator.FromHtml("#E4E7ED")), 12, 12, 8, 8);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private void OpenFolder() =>
        Process.Start(new ProcessStartInfo { FileName = _store.RootPath, UseShellExecute = true });

    // Opens a small capture window; on save, persists and re-registers the hotkey.
    private void ChangeHotkey()
    {
        var captured = HotkeyCaptureWindow.Capture(_config.Hotkey);
        if (string.IsNullOrEmpty(captured)) return;
        _config.Hotkey = captured;
        _config.Save();
        RegisterHotkeyFromConfig();
        _tray.Text = $"PasteWheel  —  {_config.Hotkey}";
    }

    // ---------- run at startup ----------

    private static RegistryKey OpenRunKey() =>
        Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)!;

    private static bool IsRunAtStartup()
    {
        using var key = OpenRunKey();
        return key?.GetValue(RunKeyName) != null;
    }

    private static void SetRunAtStartup(bool enable)
    {
        using var key = OpenRunKey();
        if (key == null) return;
        if (enable)
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (exe != null) key.SetValue(RunKeyName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(RunKeyName, throwOnMissingValue: false);
        }
    }

    // ---------- live reload ----------

    private void WatchFolder(string root)
    {
        try
        {
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            };
            FileSystemEventHandler reload = (_, _) => Dispatcher.BeginInvoke(() => _store.Reload());
            _watcher.Changed += reload;
            _watcher.Created += reload;
            _watcher.Deleted += reload;
            _watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() => _store.Reload());
        }
        catch { /* watching is best-effort */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        NativeMethods.UnregisterHotKey(_msgSink?.Handle ?? IntPtr.Zero, HotkeyId);
        _tray?.Dispose();
        _watcher?.Dispose();
        base.OnExit(e);
    }
}
