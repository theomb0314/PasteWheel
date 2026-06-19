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
    // --- palette (Teenage-Engineering inspired: flat, neutral, one accent) ---
    private static readonly Color BackdropColor = Color.FromRgb(0x0E, 0x10, 0x16);
    private static readonly Color SliceColor = Color.FromRgb(0x21, 0x24, 0x2D);
    private static readonly Color FolderColor = Color.FromRgb(0x2A, 0x2E, 0x3A);
    private static readonly Color HubColor = Color.FromRgb(0x16, 0x18, 0x20);
    private static readonly Color AccentColor = Color.FromRgb(0xFF, 0x5A, 0x1F);
    private static readonly Color TextColor = Color.FromRgb(0xF4, 0xF5, 0xF7);
    private static readonly Color DimColor = Color.FromRgb(0xAE, 0xB4, 0xC0);

    private const double BottomDeg = 180;   // 6 o'clock: where the ring starts
    private const double RecentArcDeg = 26;  // ~7% of the circle, pinned at the bottom
    private const double GapPx = 3.0;
    private const double Pad = 56;

    private readonly PasteStore _store;
    private readonly Config _config;
    private readonly Stack<PasteNode> _stack = new();
    private PasteNode _current;
    private int _selected = -1;   // index into _members, or -1 none, -2 recent wedge

    private double _cx, _cy, _outerR, _innerR, _centerR, _opacity = 1.0;

    // One drawn wedge: a node plus its angular span (degrees, clockwise from top).
    private readonly List<(PasteNode node, double start, double end)> _members = new();
    private readonly List<Path> _slices = new();
    private Path? _recentPath;
    private (double start, double end) _recentSpan;
    private bool _hasRecent;
    private readonly List<(Rect rect, int depth)> _crumbs = new();
    private StackPanel? _hub;

    // The folder chain (disk paths) of the last paste, for the recent wedge.
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

        Opacity = 1;
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
        if (_kbHook == IntPtr.Zero) { _kbProc = KeyboardHook; _kbHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _kbProc, hMod, 0); }
        if (_mouseHook == IntPtr.Zero) { _mouseProc = MouseHook; _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, hMod, 0); }
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
            if (IsOutsideDisc(m.x, m.y)) Dispatcher.BeginInvoke(Dismiss);
        }
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private bool IsOutsideDisc(int screenX, int screenY)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double cx = (Left + Width / 2) * dpi.DpiScaleX;
        double cy = (Top + Height / 2) * dpi.DpiScaleY;
        double r = _outerR * dpi.DpiScaleX;
        return Math.Sqrt(Math.Pow(screenX - cx, 2) + Math.Pow(screenY - cy, 2)) > r;
    }

    // ---------- rendering ----------

    private void Render()
    {
        WheelCanvas.Children.Clear();
        _slices.Clear();
        _members.Clear();
        _crumbs.Clear();
        _recentPath = null;
        _hasRecent = _stack.Count == 0 && _recentChain.Count > 0;

        AddCircle(_outerR + 8, Tinted(BackdropColor, 0.92));

        var slots = BuildSlots(_current.Children);
        if (slots.Count == 0)
            WheelCanvas.Children.Add(Label("(empty folder)", _cx, _cy - 8, Tinted(DimColor, 1), 15, center: true));
        else
        {
            // Content fills the circle from the bottom, clockwise, leaving a small
            // arc at the bottom for the recent wedge when one exists.
            double recentArc = _hasRecent ? RecentArcDeg : 0;
            double contentStart = BottomDeg + recentArc / 2;
            double slotSweep = (360 - recentArc) / slots.Count;

            for (int s = 0; s < slots.Count; s++)
            {
                double slotStart = contentStart + s * slotSweep;
                double sub = slotSweep / slots[s].Count;
                for (int j = 0; j < slots[s].Count; j++)
                    AddSlice(slots[s][j], slotStart + j * sub, slotStart + (j + 1) * sub);
            }
        }

        if (_hasRecent) AddRecentWedge();
        AddHub();
        AddBreadcrumb();
        UpdateSelectionVisuals();
    }

    // Groups siblings into slots: same SlotKey share a slot (split into sub-wedges).
    private static List<List<PasteNode>> BuildSlots(List<PasteNode> children)
    {
        var slots = new List<List<PasteNode>>();
        var byKey = new Dictionary<int, int>();
        foreach (var c in children)
        {
            if (c.SlotKey is int k && byKey.TryGetValue(k, out int i)) slots[i].Add(c);
            else { slots.Add(new List<PasteNode> { c }); if (c.SlotKey is int k2) byKey[k2] = slots.Count - 1; }
        }
        return slots;
    }

    private void AddSlice(PasteNode node, double start, double end)
    {
        int index = _members.Count;
        _members.Add((node, start, end));

        var path = new Path
        {
            Data = SliceGeometry(start, end, _innerR, _outerR),
            Fill = SliceFill(node),
            Stroke = Tinted(Colors.White, 0.10),
            StrokeThickness = 1,
            Cursor = Cursors.Hand,
        };
        WheelCanvas.Children.Add(path);
        _slices.Add(path);

        double mid = (start + end) / 2;
        var lp = OnCircle(mid, (_innerR + _outerR) / 2);
        Color textOn = node.Swatch is { } sc ? BestTextColor(sc)
                     : node.Accent is { } ac && node.IsFolder ? BestTextColor(ac) : TextColor;

        // Width the label to the wedge's arc so thin shared wedges trim instead of overflow.
        double arc = (_innerR + _outerR) / 2 * (end - start) * Math.PI / 180;
        double w = Math.Clamp(arc * 0.92, 38, _outerR * 0.66);

        var panel = new StackPanel { Width = w, IsHitTestVisible = false };
        if (node.IsFolder && !string.IsNullOrEmpty(node.Icon))
            panel.Children.Add(Text(node.Icon!, textOn, 20, align: TextAlignment.Center));
        panel.Children.Add(Text(node.IsFolder ? node.Label + "  ›" : node.Label, textOn, 14, bold: true, align: TextAlignment.Center, wrap: true));
        if (node.Kind == PasteKind.Hex)
            panel.Children.Add(Text(node.Content.ToUpperInvariant(), Color.FromArgb(0xCC, textOn.R, textOn.G, textOn.B), 11, mono: true, align: TextAlignment.Center));

        panel.Measure(new Size(w, 90));
        Canvas.SetLeft(panel, lp.X - w / 2);
        Canvas.SetTop(panel, lp.Y - panel.DesiredSize.Height / 2);
        WheelCanvas.Children.Add(panel);

        if (index < 9)
        {
            var bp = OnCircle(mid, _outerR - 17);
            WheelCanvas.Children.Add(Label((index + 1).ToString(), bp.X, bp.Y - 9,
                new SolidColorBrush(Color.FromArgb(0x88, textOn.R, textOn.G, textOn.B)), 11, center: true, mono: true));
        }
    }

    private Brush SliceFill(PasteNode node)
    {
        if (node.Swatch is { } c) return new SolidColorBrush(c);                 // colour: opaque
        if (node.Kind == PasteKind.Image) return ImageFill(node.Path) ?? Tinted(SliceColor, 1);
        if (node.IsFolder && node.Accent is { } a) return Tinted(a, 1);          // per-folder accent
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

    // The thin recent wedge pinned at the bottom (jumps back to the last folder).
    private void AddRecentWedge()
    {
        _recentSpan = (BottomDeg - RecentArcDeg / 2, BottomDeg + RecentArcDeg / 2);
        _recentPath = new Path
        {
            Data = SliceGeometry(_recentSpan.start, _recentSpan.end, _innerR, _outerR),
            Fill = Tinted(AccentColor, 0.18),
            Stroke = new SolidColorBrush(AccentColor),
            StrokeThickness = 1.5,
            Cursor = Cursors.Hand,
        };
        WheelCanvas.Children.Add(_recentPath);

        var lp = OnCircle(BottomDeg, (_innerR + _outerR) / 2);
        WheelCanvas.Children.Add(Label("⟲", lp.X, lp.Y - 12, new SolidColorBrush(AccentColor), 18, center: true));
    }

    private void AddHub()
    {
        var ring = new Ellipse
        {
            Width = _centerR * 2, Height = _centerR * 2,
            Fill = Tinted(HubColor, 0.98),
            Stroke = _stack.Count > 0 ? new SolidColorBrush(AccentColor) : Tinted(Colors.White, 0.12),
            StrokeThickness = _stack.Count > 0 ? 2 : 1,
            Cursor = Cursors.Hand,
        }.At(_cx - _centerR, _cy - _centerR);
        WheelCanvas.Children.Add(ring);

        _hub = new StackPanel { IsHitTestVisible = false, Width = _centerR * 1.7 };
        WheelCanvas.Children.Add(_hub);
    }

    // Clickable breadcrumb along the bottom; click a crumb to jump up to that level.
    private void AddBreadcrumb()
    {
        var chain = _stack.Reverse().Append(_current).ToList(); // [root, …, current]
        double y = _cy + _outerR + 20;

        var pieces = new List<(string text, int depth)>();
        for (int i = 0; i < chain.Count; i++)
        {
            if (i > 0) pieces.Add(("  ›  ", -1));
            pieces.Add((i == 0 ? "Home" : chain[i].Label, i));
        }

        double total = pieces.Sum(p => Measure(p.text, 13, bold: true));
        double x = _cx - total / 2;
        foreach (var (text, depth) in pieces)
        {
            double w = Measure(text, 13, bold: true);
            bool active = depth == chain.Count - 1;
            var tb = new TextBlock
            {
                Text = text, FontSize = 13, FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = depth < 0 ? Tinted(DimColor, 0.6)
                           : active ? new SolidColorBrush(TextColor) : new SolidColorBrush(AccentColor),
                Cursor = depth >= 0 && !active ? Cursors.Hand : Cursors.Arrow,
            };
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
            WheelCanvas.Children.Add(tb);
            if (depth >= 0) _crumbs.Add((new Rect(x, y, w, 20), depth));
            x += w;
        }
    }

    // ---------- selection / hub preview ----------

    private void UpdateSelectionVisuals()
    {
        for (int i = 0; i < _slices.Count; i++)
        {
            bool sel = i == _selected;
            _slices[i].Stroke = sel ? new SolidColorBrush(AccentColor) : Tinted(Colors.White, 0.10);
            _slices[i].StrokeThickness = sel ? 2.5 : 1;
        }
        if (_recentPath != null)
            _recentPath.StrokeThickness = _selected == -2 ? 3 : 1.5;

        _hub!.Children.Clear();
        if (_selected == -2)
            FillHub(null, "⟲ Recent", _recentLabel);
        else if (_selected >= 0 && _selected < _members.Count)
        {
            var n = _members[_selected].node;
            FillHub(n.Swatch, n.Label, n.Preview);
        }
        else
            FillHub(null, _stack.Count > 0 ? "↩ Back" : "Esc to close", null, hint: true);
    }

    // Lays out the centred hub content: optional swatch, a title, and a value line.
    private void FillHub(Color? swatch, string title, string? value, bool hint = false)
    {
        if (swatch is { } c)
            _hub!.Children.Add(new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(c),
                BorderBrush = Tinted(Colors.White, 0.25), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6),
            });

        _hub!.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = hint ? Tinted(DimColor, 1) : new SolidColorBrush(TextColor),
            FontSize = 15, FontWeight = hint ? FontWeights.Normal : FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"),
            TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = _centerR * 1.7,
        });

        if (!string.IsNullOrEmpty(value))
            _hub!.Children.Add(new TextBlock
            {
                Text = value,
                Foreground = new SolidColorBrush(DimColor),
                FontSize = 12, FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = _centerR * 1.7,
                Margin = new Thickness(0, 4, 0, 0),
            });

        _hub.Measure(new Size(_centerR * 1.7, _centerR * 2));
        Canvas.SetLeft(_hub, _cx - _hub.Width / 2);
        Canvas.SetTop(_hub, _cy - _hub.DesiredSize.Height / 2);
    }

    private void SetSelected(int index)
    {
        if (index == _selected) return;
        _selected = index;
        UpdateSelectionVisuals();
    }

    // ---------- input ----------

    private void OnMouseMove(object sender, MouseEventArgs e) => SetSelected(HitTest(e.GetPosition(WheelCanvas)));

    // Returns a member index, -2 for the recent wedge, or -1 for none.
    private int HitTest(Point p)
    {
        double dx = p.X - _cx, dy = p.Y - _cy;
        double r = Math.Sqrt(dx * dx + dy * dy);
        if (r < _centerR || r > _outerR) return -1;

        double theta = Math.Atan2(dx, -dy) * 180 / Math.PI; // clockwise from top
        if (theta < 0) theta += 360;
        if (_hasRecent && AngleContains(_recentSpan.start, _recentSpan.end, theta)) return -2;
        for (int i = 0; i < _members.Count; i++)
            if (AngleContains(_members[i].start, _members[i].end, theta)) return i;
        return -1;
    }

    private static bool AngleContains(double start, double end, double theta)
    {
        double s = ((start % 360) + 360) % 360;
        double d = ((theta - s) % 360 + 360) % 360;
        return d <= end - start;
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(WheelCanvas);
        foreach (var (rect, depth) in _crumbs)
            if (rect.Contains(p)) { NavigateToDepth(depth); return; }

        int hit = HitTest(p);
        if (hit == -2) { NavigateToRecent(); return; }
        if (hit >= 0) { Activate(hit); return; }
        if (Math.Sqrt(Math.Pow(p.X - _cx, 2) + Math.Pow(p.Y - _cy, 2)) < _centerR) NavigateUp();
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
            case VK_RETURN: if (_selected >= 0) Activate(_selected); else if (_selected == -2) NavigateToRecent(); return true;
            case VK_LEFT or VK_UP: Step(-1); return true;
            case VK_RIGHT or VK_DOWN: Step(+1); return true;
            case VK_0 or VK_NUMPAD0: NavigateToRecent(); return true;
            default:
                int digit = DigitFromVk(vk);
                if (digit >= 1 && digit <= _members.Count) { Activate(digit - 1); return true; }
                return false;
        }
    }

    private void Step(int dir)
    {
        int n = _members.Count;
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
        if (index < 0 || index >= _members.Count) return;
        var node = _members[index].node;
        if (node.IsFolder) { _stack.Push(_current); _current = node; _selected = -1; Render(); }
        else { RememberRecent(); Dismiss(); OnPaste?.Invoke(node); }
    }

    // Captures the folder chain of the just-used paste for the recent wedge.
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

    // Jumps to a level in the current breadcrumb (0 = Home/root).
    private void NavigateToDepth(int depth)
    {
        var chain = _stack.Reverse().Append(_current).ToList();
        if (depth < 0 || depth >= chain.Count) return;
        _stack.Clear();
        for (int i = 0; i < depth; i++) _stack.Push(chain[i]);
        _current = chain[depth];
        _selected = -1;
        Render();
    }

    private void NavigateUp()
    {
        if (_stack.Count > 0) { _current = _stack.Pop(); _selected = -1; Render(); }
        else Dismiss();
    }

    // ---------- geometry / drawing helpers ----------

    // Annular sector with uniform-width (parallel-edged) gaps between wedges.
    private Geometry SliceGeometry(double startDeg, double endDeg, double inner, double outer)
    {
        if (endDeg - startDeg >= 359.9)
            return new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(new Point(_cx, _cy), outer, outer),
                new EllipseGeometry(new Point(_cx, _cy), inner, inner));

        double outerOff = GapPx / 2 / outer * 180 / Math.PI;
        double innerOff = GapPx / 2 / inner * 180 / Math.PI;
        var p1 = OnCircle(startDeg + outerOff, outer);
        var p2 = OnCircle(endDeg - outerOff, outer);
        var p3 = OnCircle(endDeg - innerOff, inner);
        var p4 = OnCircle(startDeg + innerOff, inner);
        bool large = endDeg - startDeg > 180;

        var fig = new PathFigure { StartPoint = p1, IsClosed = true, IsFilled = true };
        fig.Segments.Add(new ArcSegment(p2, new Size(outer, outer), 0, large, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(p3, true));
        fig.Segments.Add(new ArcSegment(p4, new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true));
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
        MaxHeight = 42,
    };

    private TextBlock Label(string text, double x, double y, Brush fill, double size,
                            bool center = false, bool mono = false)
    {
        var tb = new TextBlock
        {
            Text = text, Foreground = fill, FontSize = size,
            FontFamily = mono ? new FontFamily("Cascadia Mono, Consolas") : new FontFamily("Segoe UI"),
            IsHitTestVisible = false, TextAlignment = TextAlignment.Center,
        };
        Canvas.SetTop(tb, y);
        if (center) { tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); Canvas.SetLeft(tb, x - tb.DesiredSize.Width / 2); }
        else Canvas.SetLeft(tb, x);
        return tb;
    }

    private double Measure(string text, double size, bool bold)
    {
        var tb = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, FontFamily = new FontFamily("Segoe UI") };
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return tb.DesiredSize.Width;
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
