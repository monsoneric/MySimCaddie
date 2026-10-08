using System.Diagnostics;
using System.Text;

namespace MySimCaddie.Core.Platform;

/// <summary>
/// Presses a button in another program's pop-up, e.g. "Play!" in GSPro's "GSPro Configuration" window.
/// Tries the standard Windows button messages first, then UI Automation (wired up by the WPF app).
/// </summary>
public static class AutoClicker
{
    /// <summary>UI Automation fallback for buttons that aren't classic Windows controls (set by the app).</summary>
    public static Func<IntPtr, string, bool>? FallbackClick { get; set; }

    /// <summary>"&amp;Play!" → "play"</summary>
    public static string Normalize(string? text) =>
        (text ?? "").Replace("&", "").Trim().TrimEnd('!', '.', '…').Trim().ToLowerInvariant();

    /// <returns>true once the window has been dismissed by a click.</returns>
    public static async Task<bool> ClickAsync(string windowTitle, string buttonText, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        int attempt = 0;
        bool loggedControls = false;
        IntPtr lastDialog = IntPtr.Zero;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var dialog = FindWindowByTitle(windowTitle);
            if (dialog != IntPtr.Zero)
            {
                if (dialog != lastDialog)
                {
                    Log.Info($"Found \"{GetText(dialog)}\" window; pressing \"{buttonText}\"");
                    lastDialog = dialog;
                    attempt = 0;
                    await Task.Delay(800, ct); // let it finish drawing and enabling its buttons
                }

                if (TryClick(dialog, buttonText, attempt++))
                {
                    // Confirm it actually went away.
                    for (int i = 0; i < 12; i++)
                    {
                        await Task.Delay(250, ct);
                        if (!Native.IsWindow(dialog) || !Native.IsWindowVisible(dialog))
                        {
                            Log.Info($"Pressed \"{buttonText}\" (method {attempt})");
                            return true;
                        }
                    }

                    Log.Warn($"\"{windowTitle}\" is still open after pressing \"{buttonText}\" (method {attempt}); trying another way");
                }
                else if (!loggedControls)
                {
                    loggedControls = true;
                    Log.Warn($"No \"{buttonText}\" button found in \"{windowTitle}\". Controls: {DescribeChildren(dialog)}");
                }
            }

            await Task.Delay(500, ct);
        }

        return false;
    }

    private static bool TryClick(IntPtr dialog, string buttonText, int attempt)
    {
        var button = FindChildButton(dialog, buttonText);
        int method = attempt % 3;

        if (button != IntPtr.Zero && method < 2)
        {
            if (!Native.IsWindowEnabled(button)) return false;
            Native.SetForegroundWindow(dialog);

            if (method == 0)
            {
                // What the dialog sees when the button is clicked.
                int id = Native.GetDlgCtrlID(button);
                var wParam = (IntPtr)((Native.BN_CLICKED << 16) | (id & 0xFFFF));
                return Native.PostMessage(dialog, Native.WM_COMMAND, wParam, button);
            }

            return Native.PostMessage(button, Native.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        }

        try
        {
            return FallbackClick?.Invoke(dialog, buttonText) ?? false;
        }
        catch (Exception ex)
        {
            Log.Error("UI Automation click failed", ex);
            return false;
        }
    }

    private static IntPtr FindWindowByTitle(string titleContains)
    {
        if (string.IsNullOrWhiteSpace(titleContains)) return IntPtr.Zero;
        int self = Environment.ProcessId;
        IntPtr found = IntPtr.Zero;

        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == self) return true;
            if (GetText(hwnd).Contains(titleContains.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                found = hwnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    private static IntPtr FindChildButton(IntPtr dialog, string buttonText)
    {
        var want = Normalize(buttonText);
        IntPtr found = IntPtr.Zero;
        Native.EnumChildWindows(dialog, (child, _) =>
        {
            if (Normalize(GetText(child)) == want && Native.IsWindowVisible(child))
            {
                found = child;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string DescribeChildren(IntPtr dialog)
    {
        var parts = new List<string>();
        Native.EnumChildWindows(dialog, (child, _) =>
        {
            var cls = new StringBuilder(128);
            Native.GetClassName(child, cls, cls.Capacity);
            parts.Add($"[{cls}] \"{GetText(child)}\"");
            return parts.Count < 40;
        }, IntPtr.Zero);
        return parts.Count == 0 ? "(none — not a classic Windows dialog)" : string.Join(", ", parts);
    }

    private static string GetText(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        Native.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
