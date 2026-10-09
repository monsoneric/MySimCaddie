using System.Runtime.InteropServices;
using MySimCaddie.Core.Config;

namespace MySimCaddie.Core.Platform;

/// <summary>
/// Replays recorded clicks in a program's window, e.g. GSPro's menu buttons for the driving range. GSPro's menus are
/// drawn by the game engine, not Windows buttons, so the only way in is a real mouse click at the right spot.
/// </summary>
public static class ClickPlayer
{
    public static async Task<bool> PlayAsync(string processName, IReadOnlyList<ReplayClick> clicks, CancellationToken ct)
    {
        for (int i = 0; i < clicks.Count; i++)
        {
            var click = clicks[i];
            await Task.Delay(Math.Clamp(click.DelayMs, 300, 60_000), ct);

            var hwnd = WindowPlacer.FindMainWindow(processName);
            if (hwnd == IntPtr.Zero)
            {
                Log.Warn($"Click replay: no {processName} window; stopping");
                return false;
            }

            Native.GetWindowRect(hwnd, out var r);
            var pt = new Native.POINT
            {
                X = r.Left + (int)Math.Round(click.X * (r.Right - r.Left)),
                Y = r.Top + (int)Math.Round(click.Y * (r.Bottom - r.Top)),
            };

            if (!await EnsureOnTopAsync(hwnd, pt, ct))
            {
                Log.Warn($"Click replay: something else is covering {processName} at click {i + 1} ({pt.X},{pt.Y}); stopping");
                return false;
            }

            Native.GetCursorPos(out var before);
            Native.SetCursorPos(pt.X, pt.Y);
            await Task.Delay(80, ct); // let the game see the cursor arrive (hover) before the press
            Send(Native.MOUSEEVENTF_LEFTDOWN);
            await Task.Delay(70, ct);
            Send(Native.MOUSEEVENTF_LEFTUP);
            Log.Info($"Click replay: click {i + 1}/{clicks.Count} at {click.X:P0} × {click.Y:P0} of {processName} ({pt.X},{pt.Y})");

            await Task.Delay(150, ct);
            Native.SetCursorPos(before.X, before.Y);
        }

        return true;
    }

    /// <summary>Brings the program forward if needed and checks it's the window under the click point.</summary>
    private static async Task<bool> EnsureOnTopAsync(IntPtr hwnd, Native.POINT pt, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (Native.GetAncestor(Native.WindowFromPoint(pt), Native.GA_ROOT) == hwnd) return true;

            // Windows only lets the foreground program hand over focus; a tap of Alt counts as input and unlocks it.
            Native.keybd_event(Native.VK_MENU, 0, 0, UIntPtr.Zero);
            Native.keybd_event(Native.VK_MENU, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            Native.SetForegroundWindow(hwnd);
            await Task.Delay(400, ct);
        }

        return Native.GetAncestor(Native.WindowFromPoint(pt), Native.GA_ROOT) == hwnd;
    }

    private static void Send(uint flags)
    {
        var input = new[] { new Native.INPUT { type = Native.INPUT_MOUSE, mi = new Native.MOUSEINPUT { dwFlags = flags } } };
        Native.SendInput(1, input, Marshal.SizeOf<Native.INPUT>());
    }
}
