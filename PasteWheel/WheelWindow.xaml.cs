using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WinFormsCursor = System.Windows.Forms.Cursor;
using WinFormsScreen = System.Windows.Forms.Screen;

namespace PasteWheel;

public partial class WheelWindow : Window
{
    // --- palette --- these are all tinted by the global opacity, except for hex swatches and image fills which are opaque.
    private static readonly Color BackdropColor = Color.FromRgb(0x0E, 0x10, 0x16);
    private static readonly Color SliceColor = Color.FromRgb(0x21, 0x24, 0x2D); 
    private static readonly Color FolderColor = Color.FromRgb(0x2A, 0x2E, 0x3A); 
    private static readonly Color HubColor = Color.FromRgb(0x16, 0x18, 0x20); 
    private static readonly Color AccentColor = Color.FromRgb(0xFF, 0x5A, 0x1F); 
    private static readonly Color TextColor = Color.FromRgb(0xF2, 0xF3, 0xF5); 
    private static readonly Color DimColor = Color.FromRgb(0x9A, 0xA0, 0xAC); 

    private readonly PasteStore _store;
    private readonly Config _config;
    private readonly Stack<PasteNode> _stack = new();
    private PasteNode _current;
    private int _selected = -1;

    // Geometry, recomputed from config each time the wheel opens.
    private double _cx, _cy, _outerR, _innerR, _centerR;
    private double _opacity = 1.0;
    private const double GapPx = 3.0;
    private const double Pad = 52;

    private readonly List<Path> _slices = new();
    private TextBlock? _previewName, _previewValue;
    private Border? _previewSwatch;
    private Rect _recentRect = Rect.Empty;

    // The folder chain (disk paths) of the last paste, for the "recent" chip.
    private List<string> _recentChain = new();
    private string _recentLabel = "";

    private IntPtr _kbHook, _mouseHook;
    private NativeMethods.HookProc? _kbProc, _mouseProc;

    /// Raised when the user picks a paste entry. App performs the actual paste.
    public Action<PasteNode>? OnPaste;

    public WheelWindow(PasteStore store, Config config)
    {
        InitializeComponent();
        _store = store;
        _config = config;
        _current = store.Root;

        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnClick;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Never activate / steal focus, so the app you came from keeps keyboard focus.
        NativeMethods.MakeNoActivate(NativeMethods.GetHandle(this));
    }

    // ---------- show / hide ----------

    public void ShowAt()
    {
        _store.Reload();
        _current = _store.Root;
        _stack.Clear();
        _selected = -1;

        Layout();
        if (!IsVisible) Show();
        PositionAtCursor();
        Render();

        Opacity = 1;            // base value, so the window is never left invisible
        Show();                 // shown without activation (ShowActivated=False)
        InstallHooks();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(80))));
    }

    /// Dismiss used by the tray/hotkey toggle, Esc, paste, and click-outside.
    public void Dismiss()
    {
        RemoveHooks();
        if (IsVisible) Hide();
    }

    // Reads size + opacity from config and derives the ring geometry.
    private void Layout()
    {
        _opacity = Math.Clamp(_config.Opacity, 0.2, 1.0);
        double diameter = SizeToDiameter(_config.Size);
        double win = diameter + Pad * 2;
        Width = win; Height = win;
        WheelCanvas.Width = win; WheelCanvas.Height = win;
        _cx = _cy = win / 2;
        _outerR = diameter / 2;
        _innerR = _outerR * 0.40;
        _centerR = _outerR * 0.345;
    }

    private static double SizeToDiameter(string size) => size?.Trim().ToLowerInvariant() switch
    {
        "small" => 460,
        "large" => 760,
        "medium" or null or "" => 600,
        var s => double.TryParse(s, out var d) ? Math.Clamp(d, 320, 1100) : 600,
    };

    private void PositionAtCursor()
    {
        var src = PresentationSource.FromVisual(this);
        var toDip = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

        var p = WinFormsCursor.Position;
        var wa = WinFormsScreen.FromPoint(p).WorkingArea;
        var cur = toDip.Transform(new Point(p.X, p.Y));
        var min = toDip.Transform(new Point(wa.Left, wa.Top));
        var max = toDip.Transform(new Point(wa.Right, wa.Bottom));

        Left = Math.Max(min.X, Math.Min(cur.X - Width / 2, max.X - Width));
        Top = Math.Max(min.Y, Math.Min(cur.Y - Height / 2, max.Y - Height));
    }

    // ---------- hooks ----------

    private void InstallHooks()
    {
        var hMod = NativeMethods.GetModuleHandle(null);
        if (_kbHook == IntPtr.Zero)
        {
            _kbProc = KeyboardHook;
            _kbHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _kbProc, hMod, 0);
        }
        if (_mouseHook == IntPtr.Zero)
        {
            _mouseProc = MouseHook;
            _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, hMod, 0);
        }
    }

    private void RemoveHooks()
    {
        if (_kbHook != IntPtr.Zero) { NativeMethods.UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; _kbProc = null; }
        if (_mouseHook != IntPtr.Zero) { NativeMethods.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; _mouseProc = null; }
    }

    // Number/arrow keys drive the wheel without it ever having focus.
    private IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == NativeMethods.WM_KEYDOWN || wParam == NativeMethods.WM_SYSKEYDOWN))
        {
            var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            if (HandleVk((int)data.vkCode)) return (IntPtr)1; // swallow keys the wheel uses
        }
        return NativeMethods.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    // A click outside the wheel disc dismisses it (the click still reaches the app).
    private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == NativeMethods.WM_LBUTTONDOWN ||
                           wParam == NativeMethods.WM_RBUTTONDOWN ||
                           wParam == NativeMethods.WM_MBUTTONDOWN))
        {
            var m = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            if (IsOutsideDisc(m.x, m.y))
                Dispatcher.BeginInvoke(Dismiss);
        }
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private bool IsOutsideDisc(int screenX, int screenY)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double cx = (Left + Width / 2) * dpi.DpiScaleX;
        double cy = (Top + Height / 2) * dpi.DpiScaleY;
        double r = _outerR * dpi.DpiScaleX;
        double dist = Math.Sqrt(Math.Pow(screenX - cx, 2) + Math.Pow(screenY - cy, 2));
        return dist > r;
    }

    // ---------- rendering ----------

    private void Render()
    {
        WheelCanvas.Children.Clear();
        _slices.Clear();
        _recentRect = Rect.Empty;

        // Backdrop disc.
        AddCircle(_outerR + 8, Tinted(BackdropColor, 0.92));

        var items = _current.Children;
        int n = items.Count;
        if (n == 0)
            WheelCanvas.Children.Add(Label("(empty folder)", _cx, _cy - 8, Tinted(DimColor, 1), 15, center: true));
        else
        {
            double sweep = 360.0 / n;
            for (int i = 0; i < n; i++)
                AddSlice(items[i], i, i * sweep, (i + 1) * sweep);
        }

        AddCenter();
        AddBreadcrumb();
        AddRecentChip();
        UpdateSelectionVisuals();
    }

    private void AddSlice(PasteNode node, int index, double start, double end)
    {
        Brush fill = SliceFill(node, index);
        var path = new Path
        {
            Data = SliceGeometry(start, end),
            Fill = fill,
            Stroke = Tinted(Colors.White, 0.10),
            StrokeThickness = 1,
            Cursor = Cursors.Hand,
        };
        WheelCanvas.Children.Add(path);
        _slices.Add(path);

        double mid = (start + end) / 2;
        var lp = OnCircle(mid, (_innerR + _outerR) / 2);

        Color textOn = node.Swatch is { } sc ? BestTextColor(sc)
                     : node.Accent is { } ac && node.IsFolder ? BestTextColor(ac)
                     : TextColor;

        var panel = new StackPanel { Width = _outerR * 0.62 };

        // Folder icon glyph (from _folder.json) sits above the label.
        if (node.IsFolder && !string.IsNullOrEmpty(node.Icon))
            panel.Children.Add(Text(node.Icon!, textOn, 20, mono: false, align: TextAlignment.Center));

        panel.Children.Add(Text(node.IsFolder ? node.Label + "  ›" : node.Label, textOn, 14,
            bold: true, align: TextAlignment.Center, wrap: true));

        if (node.Kind == PasteKind.Hex)
            panel.Children.Add(Text(node.Content.ToUpperInvariant(),
                Color.FromArgb(0xCC, textOn.R, textOn.G, textOn.B), 11, mono: true, align: TextAlignment.Center));

        panel.IsHitTestVisible = false;
        panel.Measure(new Size(_outerR * 0.62, 80));
        Canvas.SetLeft(panel, lp.X - panel.Width / 2);
        Canvas.SetTop(panel, lp.Y - panel.DesiredSize.Height / 2);
        WheelCanvas.Children.Add(panel);

        // Number badge near the rim (1–9) for keyboard reference.
        if (index < 9)
        {
            var bp = OnCircle(mid, _outerR - 18);
            WheelCanvas.Children.Add(Label((index + 1).ToString(), bp.X, bp.Y - 9,
                new SolidColorBrush(Color.FromArgb(0x88, textOn.R, textOn.G, textOn.B)), 11, center: true, mono: true));
        }
    }

    // Picks a slice's fill: hex swatch / image thumbnail (opaque), or accent/neutral.
    private Brush SliceFill(PasteNode node, int index)
    {
        if (node.Swatch is { } c) return new SolidColorBrush(c);            // colour: opaque
        if (node.Kind == PasteKind.Image) return ImageFill(node.Path) ?? Tinted(SliceColor, 1);
        if (node.IsFolder && node.Accent is { } a) return Tinted(a, 1);     // per-folder accent
        return Tinted(node.IsFolder ? FolderColor : SliceColor, 1);
    }

    private static Brush? ImageFill(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 160;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
        }
        catch { return null; }
    }

    private void AddCenter()
    {
        bool inFolder = _stack.Count > 0;
        var ring = new Ellipse
        {
            Width = _centerR * 2, Height = _centerR * 2,
            Fill = Tinted(HubColor, 0.98),
            Stroke = inFolder ? new SolidColorBrush(AccentColor) : Tinted(Colors.White, 0.12),
            StrokeThickness = inFolder ? 2 : 1,
            Cursor = Cursors.Hand,
        }.At(_cx - _centerR, _cy - _centerR);
        WheelCanvas.Children.Add(ring);

        WheelCanvas.Children.Add(Label(inFolder ? "↩  Back" : "Esc",
            _cx, _cy - _centerR * 0.62, inFolder ? new SolidColorBrush(TextColor) : Tinted(DimColor, 1), 12, center: true));

        _previewSwatch = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(5),
            BorderBrush = Tinted(Colors.White, 0.25), BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed,
        };
        Canvas.SetLeft(_previewSwatch, _cx - 13);
        Canvas.SetTop(_previewSwatch, _cy - 30);
        WheelCanvas.Children.Add(_previewSwatch);

        _previewName = Label("", _cx, _cy + 2, new SolidColorBrush(TextColor), 14, center: true, bold: true);
        WheelCanvas.Children.Add(_previewName);
        _previewValue = Label("", _cx, _cy + 24, Tinted(DimColor, 1), 11, center: true, mono: true);
        WheelCanvas.Children.Add(_previewValue);
    }

    private void AddBreadcrumb()
    {
        var crumbs = _stack.Reverse().Select(s => s.Label).Append(_current.Label);
        string trail = _stack.Count == 0 ? "PasteWheel" : string.Join("  ›  ", crumbs);
        WheelCanvas.Children.Add(Label(trail, _cx, _cy - _outerR - 34, Tinted(DimColor, 1), 13, center: true));
    }

    // A fixed chip at root that jumps straight back to the last folder you pasted from.
    private void AddRecentChip()
    {
        if (_stack.Count > 0 || _recentChain.Count == 0) return;

        double w = _centerR * 1.5, h = 26;
        double x = _cx - w / 2, y = _cy + _centerR * 0.42;
        _recentRect = new Rect(x, y, w, h);

        var chip = new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(13),
            Background = Tinted(AccentColor, 0.16),
            BorderBrush = new SolidColorBrush(AccentColor), BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = "⟲  " + _recentLabel,
                Foreground = new SolidColorBrush(TextColor),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        chip.At(x, y);
        WheelCanvas.Children.Add(chip);
    }

    // ---------- selection ----------

    private void UpdateSelectionVisuals()
    {
        for (int i = 0; i < _slices.Count; i++)
        {
            bool sel = i == _selected;
            _slices[i].Stroke = sel ? new SolidColorBrush(AccentColor) : Tinted(Colors.White, 0.10);
            _slices[i].StrokeThickness = sel ? 2.5 : 1;
        }

        if (_selected >= 0 && _selected < _current.Children.Count)
        {
            var node = _current.Children[_selected];
            _previewName!.Text = node.Label;
            _previewValue!.Text = node.Preview;
            if (node.Swatch is { } c)
            {
                _previewSwatch!.Background = new SolidColorBrush(c);
                _previewSwatch.Visibility = Visibility.Visible;
                Place(_previewName, _cy + 10);
            }
            else
            {
                _previewSwatch!.Visibility = Visibility.Collapsed;
                Place(_previewName, _cy - 6);
            }
        }
        else
        {
            _previewName!.Text = "";
            _previewValue!.Text = "";
            _previewSwatch!.Visibility = Visibility.Collapsed;
        }
    }

    private void Place(TextBlock tb, double top) { Canvas.SetTop(tb, top); Center(tb, _cx); }

    private void SetSelected(int index)
    {
        if (index == _selected) return;
        _selected = index;
        UpdateSelectionVisuals();
    }

    // ---------- input ----------

    private void OnMouseMove(object sender, MouseEventArgs e) => SetSelected(HitTest(e.GetPosition(WheelCanvas)));

    private int HitTest(Point p)
    {
        double dx = p.X - _cx, dy = p.Y - _cy;
        double r = Math.Sqrt(dx * dx + dy * dy);
        if (r < _centerR || r > _outerR || _current.Children.Count == 0) return -1;

        double theta = Math.Atan2(dx, -dy) * 180 / Math.PI; // clockwise from top
        if (theta < 0) theta += 360;
        int idx = (int)(theta / (360.0 / _current.Children.Count));
        return Math.Min(idx, _current.Children.Count - 1);
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(WheelCanvas);
        if (_recentRect.Contains(p)) { NavigateToRecent(); return; }
        double r = Math.Sqrt(Math.Pow(p.X - _cx, 2) + Math.Pow(p.Y - _cy, 2));
        if (r < _centerR) { NavigateUp(); return; }
        int idx = HitTest(p);
        if (idx >= 0) Activate(idx);
    }

    private const int VK_ESCAPE = 0x1B, VK_BACK = 0x08, VK_RETURN = 0x0D;
    private const int VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;
    private const int VK_0 = 0x30, VK_9 = 0x39, VK_NUMPAD0 = 0x60, VK_NUMPAD9 = 0x69;

    // Handles a virtual key from the hook. Returns true if the wheel consumed it.
    private bool HandleVk(int vk)
    {
        switch (vk)
        {
            case VK_ESCAPE: Dismiss(); return true;
            case VK_BACK: NavigateUp(); return true;
            case VK_RETURN: if (_selected >= 0) Activate(_selected); return true;
            case VK_LEFT or VK_UP: Step(-1); return true;
            case VK_RIGHT or VK_DOWN: Step(+1); return true;
            case VK_0 or VK_NUMPAD0: NavigateToRecent(); return true;
            default:
                int digit = DigitFromVk(vk);
                if (digit >= 1 && digit <= _current.Children.Count) { Activate(digit - 1); return true; }
                return false;
        }
    }

    private void Step(int dir)
    {
        int n = _current.Children.Count;
        if (n == 0) return;
        _selected = _selected < 0 ? (dir > 0 ? 0 : n - 1) : (_selected + dir + n) % n;
        UpdateSelectionVisuals();
    }

    private static int DigitFromVk(int vk) => vk switch
    {
        >= VK_0 + 1 and <= VK_9 => vk - VK_0,
        >= VK_NUMPAD0 + 1 and <= VK_NUMPAD9 => vk - VK_NUMPAD0,
        _ => -1,
    };

    // ---------- navigation / activation ----------

    private void Activate(int index)
    {
        if (index < 0 || index >= _current.Children.Count) return;
        var node = _current.Children[index];

        if (node.IsFolder)
        {
            _stack.Push(_current);
            _current = node;
            _selected = -1;
            Render();
        }
        else
        {
            RememberRecent();
            Dismiss();
            OnPaste?.Invoke(node);
        }
    }

    // Captures the folder chain of the just-used paste for the recent chip.
    private void RememberRecent()
    {
        if (_current == _store.Root) { _recentChain = new(); _recentLabel = ""; return; }
        _recentChain = _stack.Reverse().Skip(1).Select(n => n.Path).Append(_current.Path).ToList();
        _recentLabel = _current.Label;
    }

    private void NavigateToRecent()
    {
        if (_stack.Count > 0 || _recentChain.Count == 0) return;
        foreach (var path in _recentChain)
        {
            var next = _current.Children.FirstOrDefault(c => c.IsFolder && c.Path == path);
            if (next == null) break;
            _stack.Push(_current);
            _current = next;
        }
        _selected = -1;
        Render();
    }

    private void NavigateUp()
    {
        if (_stack.Count > 0) { _current = _stack.Pop(); _selected = -1; Render(); }
        else Dismiss();
    }

    // ---------- geometry / drawing helpers ----------

    // Builds an annular sector with uniform-width (parallel-edged) gaps between wedges.
    private Geometry SliceGeometry(double startDeg, double endDeg)
    {
        if (endDeg - startDeg >= 359.9)
            return new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(new Point(_cx, _cy), _outerR, _outerR),
                new EllipseGeometry(new Point(_cx, _cy), _innerR, _innerR));

        double outerOff = (GapPx / 2) / _outerR * 180 / Math.PI;
        double innerOff = (GapPx / 2) / _innerR * 180 / Math.PI;
        var p1 = OnCircle(startDeg + outerOff, _outerR);
        var p2 = OnCircle(endDeg - outerOff, _outerR);
        var p3 = OnCircle(endDeg - innerOff, _innerR);
        var p4 = OnCircle(startDeg + innerOff, _innerR);
        bool large = (endDeg - startDeg) > 180;

        var fig = new PathFigure { StartPoint = p1, IsClosed = true, IsFilled = true };
        fig.Segments.Add(new ArcSegment(p2, new Size(_outerR, _outerR), 0, large, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(p3, true));
        fig.Segments.Add(new ArcSegment(p4, new Size(_innerR, _innerR), 0, large, SweepDirection.Counterclockwise, true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        return geo;
    }

    private Point OnCircle(double angleDeg, double r)
    {
        double rad = angleDeg * Math.PI / 180;
        return new Point(_cx + r * Math.Sin(rad), _cy - r * Math.Cos(rad));
    }

    private void AddCircle(double radius, Brush fill) =>
        WheelCanvas.Children.Add(new Ellipse { Width = radius * 2, Height = radius * 2, Fill = fill }
            .At(_cx - radius, _cy - radius));

    // Applies the global opacity to a colour (used for everything except swatches/images).
    private SolidColorBrush Tinted(Color c, double baseAlpha) =>
        new(Color.FromArgb((byte)Math.Clamp(baseAlpha * _opacity * 255, 0, 255), c.R, c.G, c.B));

    private static Color BestTextColor(Color bg)
    {
        double L = 0.2126 * Lin(bg.R) + 0.7152 * Lin(bg.G) + 0.0722 * Lin(bg.B);
        return L > 0.36 ? Color.FromRgb(0x10, 0x12, 0x1C) : Colors.White;
        static double Lin(byte c) { double s = c / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
    }

    private TextBlock Text(string text, Color fill, double size, bool bold = false, bool mono = false,
                           TextAlignment align = TextAlignment.Left, bool wrap = false) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(fill),
        FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        FontFamily = mono ? new FontFamily("Cascadia Mono, Consolas") : new FontFamily("Segoe UI"),
        TextAlignment = align,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxHeight = 40,
    };

    private TextBlock Label(string text, double x, double y, Brush fill, double size,
                            bool center = false, bool bold = false, bool mono = false)
    {
        var tb = new TextBlock
        {
            Text = text, Foreground = fill, FontSize = size,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            FontFamily = mono ? new FontFamily("Cascadia Mono, Consolas") : new FontFamily("Segoe UI"),
            IsHitTestVisible = false, TextAlignment = TextAlignment.Center,
        };
        Canvas.SetTop(tb, y);
        if (center) Center(tb, x); else Canvas.SetLeft(tb, x);
        return tb;
    }

    private static void Center(TextBlock tb, double cx)
    {
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(tb, cx - tb.DesiredSize.Width / 2);
    }
}

internal static class CanvasExt
{
    public static T At<T>(this T el, double left, double top) where T : UIElement
    {
        Canvas.SetLeft(el, left);
        Canvas.SetTop(el, top);
        return el;
    }
}
