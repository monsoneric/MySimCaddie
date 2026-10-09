using System.Diagnostics;
using MySimCaddie.Core.Config;
using MySimCaddie.Core.Displays;

namespace MySimCaddie.Core.Platform;

/// <summary>Finds an app's main window and puts it on a given display.</summary>
public static class WindowPlacer
{
    /// <summary>
    /// Wait for the process's main window, place it, then re-check a couple of times because many apps
    /// restore their own saved position a moment after they open.
    /// </summary>
    public static async Task<bool> PlaceAsync(string processName, DisplayInfo display, WindowMode mode, TimeSpan timeout, CancellationToken ct)
    {
        if (mode == WindowMode.None) return true;

        var deadline = DateTime.UtcNow + timeout;
        IntPtr hwnd = IntPtr.Zero;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            hwnd = FindMainWindow(processName);
            if (hwnd != IntPtr.Zero) break;
            await Task.Delay(400, ct);
        }

        if (hwnd == IntPtr.Zero)
        {
            Log.Warn($"No window found for {processName} within {timeout.TotalSeconds:0}s");
            return false;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            Place(hwnd, display, mode);
            await Task.Delay(1500, ct);

            // The app may have swapped to a different top-level window (splash → main).
            var current = FindMainWindow(processName);
            if (current != IntPtr.Zero && current != hwnd)
            {
                hwnd = current;
                continue;
            }

            if (IsOnDisplay(hwnd, display)) return true;
        }

        return IsOnDisplay(hwnd, display);
    }

    public static void Place(IntPtr hwnd, DisplayInfo display, WindowMode mode)
    {
        var b = display.Bounds;
        var work = display.WorkArea;

        // A maximized window must be restored before it will move monitors.
        if (Native.IsZoomed(hwnd) || Native.IsIconic(hwnd))
            Native.ShowWindow(hwnd, Native.SW_RESTORE);

        switch (mode)
        {
            case WindowMode.Move:
            {
                Native.GetWindowRect(hwnd, out var r);
                int w = Math.Min(r.Right - r.Left, work.Width), h = Math.Min(r.Bottom - r.Top, work.Height);
                Native.SetWindowPos(hwnd, IntPtr.Zero, work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h,
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
                break;
            }
            case WindowMode.Maximize:
                // Park it inside the target's work area, then maximize so it fills that monitor.
                Native.SetWindowPos(hwnd, IntPtr.Zero, work.X + 40, work.Y + 40, Math.Max(400, work.Width - 80), Math.Max(300, work.Height - 80),
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
                Native.ShowWindow(hwnd, Native.SW_MAXIMIZE);
                break;
            case WindowMode.Fill:
                // Twice: the first move can trigger a DPI change that rescales the window.
                for (int i = 0; i < 2; i++)
                    Native.SetWindowPos(hwnd, IntPtr.Zero, b.X, b.Y, b.Width, b.Height,
                        Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
                break;
        }
    }

    public static bool IsOnDisplay(IntPtr hwnd, DisplayInfo display) =>
        Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST) == display.HMonitor;

    /// <summary>Largest visible, unowned, titled top-level window belonging to any process with this name.</summary>
    /// <summary>Brings a program's main window to the front (e.g. back into GSPro from the launcher).</summary>
    public static bool BringToFront(string processName)
    {
        var hwnd = FindMainWindow(processName);
        if (hwnd == IntPtr.Zero) return false;
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);

        // Windows only lets the foreground program hand over focus; a tap of Alt counts as input and unlocks it.
        Native.keybd_event(Native.VK_MENU, 0, 0, UIntPtr.Zero);
        Native.keybd_event(Native.VK_MENU, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
        return Native.SetForegroundWindow(hwnd);
    }

    public static IntPtr FindMainWindow(string processName)
    {
        var pids = ProcessTools.GetPids(processName);
        if (pids.Count == 0) return IntPtr.Zero;

        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        Native.EnumWindows((hwnd, _) =>
        {
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains((int)pid)) return true;
            if (!Native.IsWindowVisible(hwnd)) return true;
            if (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return true;
            if (Native.GetWindowTextLength(hwnd) == 0) return true;

            Native.GetWindowRect(hwnd, out var r);
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea)
            {
                bestArea = area;
                best = hwnd;
            }

            return true;
        }, IntPtr.Zero);

        return best;
    }
}

public static class ProcessTools
{
    public static HashSet<int> GetPids(string processName)
    {
        var set = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(processName)) return set;
        foreach (var p in Process.GetProcessesByName(processName))
        {
            set.Add(p.Id);
            p.Dispose();
        }

        return set;
    }

    public static bool IsRunning(string processName) => GetPids(processName).Count > 0;

    public static async Task<bool> WaitForStartAsync(string processName, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsRunning(processName)) return true;
            await Task.Delay(500, ct);
        }

        return IsRunning(processName);
    }

    /// <summary>Polite close (like clicking X), then force-kill whatever is left after the grace period.</summary>
    public static async Task CloseAsync(string processName, TimeSpan grace)
    {
        if (string.IsNullOrWhiteSpace(processName)) return;

        var procs = Process.GetProcessesByName(processName);
        if (procs.Length == 0) return;

        Log.Info($"Closing {processName} ({procs.Length} process(es))");
        foreach (var p in procs)
        {
            try { p.CloseMainWindow(); } catch { /* may have no window */ }
        }

        var deadline = DateTime.UtcNow + grace;
        while (DateTime.UtcNow < deadline && procs.Any(p => !HasExited(p)))
            await Task.Delay(300);

        foreach (var p in procs)
        {
            try
            {
                if (!HasExited(p))
                {
                    Log.Warn($"{processName} (pid {p.Id}) didn't close; killing");
                    p.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                // Typically access denied when the app runs elevated and we don't.
                Log.Error($"Could not close {processName}", ex);
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    private static bool HasExited(Process p)
    {
        try { return p.HasExited; }
        catch { return true; }
    }
}
