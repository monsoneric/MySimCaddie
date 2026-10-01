using System.Windows;
using System.Windows.Interop;
using MySimCaddie.Core.Displays;
using MySimCaddie.Core.Platform;

namespace MySimCaddie.Services;

public static class WpfWindowTools
{
    /// <summary>Size a borderless WPF window to exactly cover a display (physical pixels).</summary>
    public static void CoverDisplay(Window window, DisplayInfo display, bool activate = false)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var b = display.Bounds;
        uint flags = Native.SWP_NOZORDER | (activate ? 0 : Native.SWP_NOACTIVATE);

        // Twice: moving onto a monitor with a different scale makes WPF resize for the new DPI.
        Native.SetWindowPos(hwnd, IntPtr.Zero, b.X, b.Y, b.Width, b.Height, flags);
        Native.SetWindowPos(hwnd, IntPtr.Zero, b.X, b.Y, b.Width, b.Height, flags);

        // And once more after WPF has processed any DPI-change layout pass.
        window.Dispatcher.InvokeAsync(() =>
            Native.SetWindowPos(hwnd, IntPtr.Zero, b.X, b.Y, b.Width, b.Height, flags),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }
}

/// <summary>System-wide hotkey (works while GSPro has focus).</summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;

    private readonly int _id;
    private HwndSource? _source;
    private IntPtr _hwnd;

    public event Action? Pressed;

    public GlobalHotkey(int id) => _id = id;

    public bool Register(Window window, uint modifiers, uint virtualKey)
    {
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        return Native.RegisterHotKey(_hwnd, _id, modifiers | MOD_NOREPEAT, virtualKey);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
        {
            Pressed?.Invoke();
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) Native.UnregisterHotKey(_hwnd, _id);
        _source?.RemoveHook(WndProc);
    }
}
