using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PasteWheel;

/// Thin P/Invoke layer for the Win32 features WPF doesn't expose: global hotkey
/// registration, synthesizing keystrokes (typing / Ctrl+V), the no-activate window
/// style, and the low-level keyboard/mouse hooks used while the wheel is open.
internal static class NativeMethods
{
    public const int WM_HOTKEY = 0x0312;

    // RegisterHotKey modifier flags.
    [Flags]
    public enum Mod : uint
    {
        None = 0x0000,
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Win = 0x0008,
        NoRepeat = 0x4000,
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ----- SendInput plumbing for typing / the simulated Ctrl+V -----

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        // The union must be sized for its largest member (MOUSEINPUT) so that
        // sizeof(INPUT) == 40 on x64; otherwise SendInput's cbSize check fails
        // and it silently inserts nothing.
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const ushort VK_MENU = 0x12;    // Alt
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;

    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint MAPVK_VK_TO_VSC = 0;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    /// <summary>
    /// Sends a clean Ctrl+V to the focused window. The modifier keys the user
    /// may still be holding (Alt/Ctrl/Shift/Win from the hotkey) are released
    /// first, otherwise the paste becomes e.g. Ctrl+Alt+V and is ignored.
    /// </summary>
    public static void SendPaste()
    {
        var inputs = new INPUT[]
        {
            KeyInput(VK_MENU, true),     // release Alt
            KeyInput(VK_SHIFT, true),    // release Shift
            KeyInput(VK_LWIN, true),     // release Win
            KeyInput(VK_RWIN, true),
            KeyInput(VK_CONTROL, true),  // release any stray Ctrl, then drive it cleanly
            KeyInput(VK_CONTROL, false), // Ctrl down
            KeyInput(VK_V, false),       // V down
            KeyInput(VK_V, true),        // V up
            KeyInput(VK_CONTROL, true),  // Ctrl up
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private const uint KEYEVENTF_UNICODE = 0x0004;

    /// <summary>
    /// Types <paramref name="text"/> as direct Unicode keystrokes into the focused
    /// control. More reliable than synthesized Ctrl+V (some WinUI/UWP apps ignore
    /// injected paste commands) and works regardless of clipboard format.
    /// </summary>
    public static void SendText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            inputs.Add(UnicodeInput(c, false));
            inputs.Add(UnicodeInput(c, true));
        }
        var arr = inputs.ToArray();
        SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
    }

    private static INPUT UnicodeInput(char c, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
            }
        }
    };

    private static INPUT KeyInput(ushort vk, bool keyUp)
    {
        // Include the hardware scan code: modern WinUI/Store apps (e.g. the new
        // Notepad) ignore synthesized input that carries only a virtual key.
        ushort scan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);
        uint flags = KEYEVENTF_SCANCODE | (keyUp ? KEYEVENTF_KEYUP : 0);
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = scan,
                    dwFlags = flags,
                }
            }
        };
    }

    /// <summary>Gets the HWND for a WPF window (used as the hotkey message sink).</summary>
    public static IntPtr GetHandle(Window window) => new WindowInteropHelper(window).Handle;

    // ----- No-activate window style (so the wheel never steals focus) -----

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public static void MakeNoActivate(IntPtr hWnd)
    {
        int ex = GetWindowLong(hWnd, GWL_EXSTYLE);
        SetWindowLong(hWnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    // ----- Low-level hooks (keyboard nav + click-outside dismiss, no focus needed) -----

    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_MBUTTONDOWN = 0x0207;

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public int x;
        public int y;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);
}
