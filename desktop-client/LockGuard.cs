using System.Runtime.InteropServices;

namespace AppleEsports.Desktop;

/// <summary>
/// Keeps the full-screen lock in charge while it is up. A game left running under it could
/// come straight back over it: TopMost was set once and never again, so a game that pinned
/// itself on top later (match start, loading screen) won; the game still had keyboard focus;
/// and Alt+Tab / the Windows key went straight back to it. Engaged only while the page is in
/// "full" mode - never during play or maintenance.
/// </summary>
internal sealed class LockGuard : IDisposable
{
    private readonly Form _form;
    private readonly System.Windows.Forms.Timer _repin = new() { Interval = 1500 };
    private LowLevelKeyboardProc? _hookProc;   // held so the GC can't collect the callback
    private IntPtr _hook = IntPtr.Zero;

    public LockGuard(Form form)
    {
        _form = form;
        _repin.Tick += (_, _) => Reassert();
    }

    public void Engage()
    {
        if (_hook == IntPtr.Zero)
        {
            _hookProc = HookCallback;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
        }
        Reassert();
        _repin.Start();
    }

    public void Release()
    {
        _repin.Stop();
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    /// <summary>Back on top of the topmost group and in front. Skipped while a dialog (the staff
    /// PIN prompt, settings) is open, or it would be pulled out from under whoever is typing.</summary>
    private void Reassert()
    {
        if (Application.OpenForms.Count > 1) return;
        SetWindowPos(_form.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        if (GetForegroundWindow() != _form.Handle)
        {
            SetForegroundWindow(_form.Handle);
            _form.Activate();
        }
    }

    /// <summary>On top of everything WITHOUT taking focus - for the "save your game" warning, so
    /// a player mid-match is warned, not thrown out of a fullscreen game five minutes early.</summary>
    public static void PinWithoutFocus(IntPtr handle, bool pin) =>
        SetWindowPos(handle, pin ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>Blocks the keys that switch away from the lock: Windows key, Alt+Tab, Alt+Esc,
    /// Ctrl+Esc. Staff shortcuts (Ctrl+Alt+Q, Ctrl+Shift+P...) use none of these.</summary>
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var vk = Marshal.ReadInt32(lParam);
            var flags = Marshal.ReadInt32(lParam, 8);
            var alt = (flags & LLKHF_ALTDOWN) != 0;
            var ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

            if (vk is VK_LWIN or VK_RWIN
                || (alt && vk is VK_TAB or VK_ESCAPE)
                || (ctrl && vk == VK_ESCAPE))
                return (IntPtr)1;
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Release();
        _repin.Dispose();
    }

    private const int WH_KEYBOARD_LL = 13;
    private const int LLKHF_ALTDOWN = 0x20;
    private const int VK_TAB = 0x09, VK_ESCAPE = 0x1B, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_CONTROL = 0x11;
    private static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
