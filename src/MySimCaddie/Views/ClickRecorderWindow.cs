using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MySimCaddie.Core;
using MySimCaddie.Core.Config;
using MySimCaddie.Core.Platform;

namespace MySimCaddie.Views;

/// <summary>
/// A small always-on-top bar that records left clicks made in one program (e.g. GSPro) as positions relative to its
/// window, plus the pause before each one, so they can be replayed after start-up.
/// </summary>
public sealed class ClickRecorderWindow : Window
{
    private readonly string _processName;
    private readonly List<ReplayClick> _clicks = new();
    private readonly TextBlock _status = new() { FontSize = 18, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Native.LowLevelMouseProc _proc; // kept alive while hooked
    private IntPtr _hook;
    private DateTime _last = DateTime.UtcNow;

    /// <summary>The recorded clicks, or null if cancelled.</summary>
    public List<ReplayClick>? Result { get; private set; }

    public ClickRecorderWindow(string processName)
    {
        _processName = processName;
        _proc = HookProc;

        Title = "Record clicks";
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowActivated = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White;

        var done = new Button { Content = "Done", FontSize = 18, Padding = new Thickness(22, 8, 22, 8), Margin = new Thickness(0, 0, 10, 0), IsDefault = true };
        var undo = new Button { Content = "Undo last", FontSize = 18, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 10, 0) };
        var cancel = new Button { Content = "Cancel", FontSize = 18, Padding = new Thickness(16, 8, 16, 8), IsCancel = true };
        done.Click += (_, _) => Finish(true);
        undo.Click += (_, _) =>
        {
            if (_clicks.Count > 0) _clicks.RemoveAt(_clicks.Count - 1);
            UpdateStatus();
        };
        cancel.Click += (_, _) => Finish(false);

        Content = new StackPanel
        {
            Margin = new Thickness(22),
            MaxWidth = 620,
            Children =
            {
                new TextBlock { Text = $"Recording clicks in {processName}", FontSize = 22, FontWeight = FontWeights.SemiBold },
                new TextBlock
                {
                    Text = $"1. Get {processName} to its main menu (start it now if it isn't running).\n" +
                           "2. Click through the menu exactly as you want it replayed, e.g. Practice, then Range. " +
                           "Leave the same pauses you'd want between clicks.\n" +
                           "3. Click Done here, then Save in Setup.\n\n" +
                           $"Only clicks inside {processName} are recorded.",
                    FontSize = 16, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
                },
                _status,
                new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), Children = { done, undo, cancel } },
            },
        };

        Loaded += (_, _) =>
        {
            _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) Log.Warn($"Click recorder: couldn't watch the mouse (error {Marshal.GetLastWin32Error()})");
            Log.Info($"Click recorder: started for {processName}");
        };
        Closed += (_, _) => Unhook();
        UpdateStatus();
    }

    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam == (IntPtr)Native.WM_LBUTTONDOWN)
        {
            var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            // Don't do window lookups inside the hook (Windows drops slow hooks); handle it right after.
            var pt = info.pt;
            Dispatcher.InvokeAsync(() => Record(pt));
        }

        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void Record(Native.POINT pt)
    {
        var root = Native.GetAncestor(Native.WindowFromPoint(pt), Native.GA_ROOT);
        if (root == IntPtr.Zero) return;
        Native.GetWindowThreadProcessId(root, out var pid);
        string name;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
        }
        catch
        {
            return;
        }

        if (!name.Equals(_processName, StringComparison.OrdinalIgnoreCase)) return;

        Native.GetWindowRect(root, out var r);
        double w = Math.Max(1, r.Right - r.Left), h = Math.Max(1, r.Bottom - r.Top);
        var now = DateTime.UtcNow;
        // The first click waits a few seconds after start-up for the menu; later ones keep the pause you left.
        var delay = _clicks.Count == 0 ? 4000 : (int)Math.Clamp((now - _last).TotalMilliseconds, 800, 30_000);
        _last = now;

        _clicks.Add(new ReplayClick { X = Math.Round((pt.X - r.Left) / w, 4), Y = Math.Round((pt.Y - r.Top) / h, 4), DelayMs = delay });
        Log.Info($"Click recorder: click {_clicks.Count} at {(pt.X - r.Left) / w:P0} × {(pt.Y - r.Top) / h:P0} after {delay} ms");
        UpdateStatus();
    }

    private void UpdateStatus() =>
        _status.Text = _clicks.Count == 0
            ? "Waiting for your first click…"
            : $"{_clicks.Count} click{(_clicks.Count == 1 ? "" : "s")} recorded.";

    private void Finish(bool keep)
    {
        Result = keep ? _clicks.ToList() : null;
        Close();
    }

    private void Unhook()
    {
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
