using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MySimCaddie.Core.Platform;

public enum AutoClickResult
{
    Clicked,
    NotFound,
    /// <summary>The pop-up belongs to a program running as administrator and MySimCaddie isn't, so Windows blocks the click.</summary>
    Blocked,
}

/// <summary>
/// Presses a button in another program's pop-up, e.g. "Play!" in GSPro's "GSPro Configuration" window.
/// Finds the pop-up by title, or by owning process + button text, then tries the standard Windows button
/// messages and finally UI Automation (wired up by the WPF app).
/// </summary>
public static class AutoClicker
{
    /// <summary>UI Automation fallback for buttons that aren't classic Windows controls (set by the app).</summary>
    public static Func<IntPtr, string, bool>? FallbackClick { get; set; }

    /// <summary>"&amp;Play!" → "play"</summary>
    public static string Normalize(string? text) =>
        (text ?? "").Replace("&", "").Trim().TrimEnd('!', '.', '…').Trim().ToLowerInvariant();

    public static async Task<AutoClickResult> ClickAsync(string windowTitle, string buttonText, IEnumerable<string> processHints,
        TimeSpan timeout, CancellationToken ct)
    {
        var hints = processHints.Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var started = DateTime.UtcNow;
        var deadline = started + timeout;
        int attempt = 0;
        bool loggedControls = false, loggedScan = false;
        IntPtr lastDialog = IntPtr.Zero;

        Log.Info($"Auto-press: watching for \"{buttonText}\" in a window titled \"{windowTitle}\" (or any {string.Join("/", hints)} window with that button)");

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var dialog = FindDialog(windowTitle, buttonText, hints);
            if (dialog != IntPtr.Zero)
            {
                if (dialog != lastDialog)
                {
                    lastDialog = dialog;
                    attempt = 0;
                    Log.Info($"Auto-press: found {Describe(dialog)}");

                    if (IsBlockedByElevation(dialog))
                    {
                        Log.Warn("Auto-press: that window belongs to a program running as administrator and MySimCaddie isn't, so Windows blocks the click");
                        return AutoClickResult.Blocked;
                    }

                    await Task.Delay(800, ct); // let it finish drawing and enabling its buttons
                }

                if (TryClick(dialog, buttonText, attempt++, out bool accessDenied))
                {
                    for (int i = 0; i < 12; i++)
                    {
                        await Task.Delay(250, ct);
                        if (!Native.IsWindow(dialog) || !Native.IsWindowVisible(dialog))
                        {
                            Log.Info($"Auto-press: pressed \"{buttonText}\" (method {attempt})");
                            return AutoClickResult.Clicked;
                        }
                    }

                    Log.Warn($"Auto-press: window still open after method {attempt}; trying another way");
                }
                else if (accessDenied)
                {
                    Log.Warn("Auto-press: Windows refused the click (access denied — the program is probably running as administrator)");
                    return AutoClickResult.Blocked;
                }
                else if (!loggedControls)
                {
                    loggedControls = true;
                    Log.Warn($"Auto-press: no \"{buttonText}\" button found. Controls: {DescribeChildren(dialog)}");
                }
            }
            else if (!loggedScan && DateTime.UtcNow - started > TimeSpan.FromSeconds(20))
            {
                loggedScan = true;
                Log.Warn($"Auto-press: nothing matched after 20s. Windows on screen: {ScanForDiagnostics(hints)}");
            }

            await Task.Delay(500, ct);
        }

        if (!loggedScan) Log.Warn($"Auto-press: gave up. Windows on screen: {ScanForDiagnostics(hints)}");
        return AutoClickResult.NotFound;
    }

    public static bool IsCurrentProcessElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    // ───────────── Finding the pop-up ─────────────

    private static IntPtr FindDialog(string titleContains, string buttonText, List<string> processHints)
    {
        int self = Environment.ProcessId;
        IntPtr byTitle = IntPtr.Zero, byProcess = IntPtr.Zero;
        var hintPids = processHints.SelectMany(ProcessTools.GetPids).ToHashSet();
        var title = (titleContains ?? "").Trim();

        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == self) return true;

            if (title.Length > 0 && GetText(hwnd).Contains(title, StringComparison.OrdinalIgnoreCase))
            {
                byTitle = hwnd;
                return false;
            }

            if (byProcess == IntPtr.Zero && hintPids.Contains((int)pid) && FindChildButton(hwnd, buttonText) != IntPtr.Zero)
                byProcess = hwnd;

            return true;
        }, IntPtr.Zero);

        return byTitle != IntPtr.Zero ? byTitle : byProcess;
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

    // ───────────── Clicking ─────────────

    private static bool TryClick(IntPtr dialog, string buttonText, int attempt, out bool accessDenied)
    {
        accessDenied = false;
        var button = FindChildButton(dialog, buttonText);
        int method = attempt % 3;

        if (button != IntPtr.Zero && method < 2)
        {
            if (!Native.IsWindowEnabled(button)) return false;
            Native.SetForegroundWindow(dialog);

            bool ok;
            if (method == 0)
            {
                // What the dialog sees when the button is clicked.
                int id = Native.GetDlgCtrlID(button);
                var wParam = (IntPtr)((Native.BN_CLICKED << 16) | (id & 0xFFFF));
                ok = Native.PostMessage(dialog, Native.WM_COMMAND, wParam, button);
            }
            else
            {
                ok = Native.PostMessage(button, Native.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            }

            if (!ok) accessDenied = Marshal.GetLastWin32Error() == 5;
            return ok;
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

    /// <summary>A normal program can't send clicks to an administrator program (Windows UIPI).</summary>
    private static bool IsBlockedByElevation(IntPtr hwnd)
    {
        if (IsCurrentProcessElevated()) return false;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        return IsProcessElevated(pid) ?? false;
    }

    /// <returns>true/false, or null if it couldn't be determined.</returns>
    private static bool? IsProcessElevated(uint pid)
    {
        var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            // Opening an administrator process's token is refused to normal programs — that itself means elevated.
            if (!Native.OpenProcessToken(process, Native.TOKEN_QUERY, out var token))
                return Marshal.GetLastWin32Error() == 5 ? true : null;
            try
            {
                return Native.GetTokenInformation(token, Native.TokenElevation, out int elevated, sizeof(int), out _)
                    ? elevated != 0
                    : null;
            }
            finally
            {
                Native.CloseHandle(token);
            }
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    // ───────────── Diagnostics for the log ─────────────

    private static string Describe(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        var cls = new StringBuilder(128);
        Native.GetClassName(hwnd, cls, cls.Capacity);
        var elevated = IsProcessElevated(pid);
        return $"\"{GetText(hwnd)}\" [{cls}] in {ProcessName(pid)} (pid {pid}, admin: {(elevated is null ? "?" : elevated.Value ? "yes" : "no")})";
    }

    /// <summary>Visible windows from the GSPro processes, plus anything with "GSPro" in its title.</summary>
    private static string ScanForDiagnostics(List<string> processHints)
    {
        var hintPids = processHints.SelectMany(ProcessTools.GetPids).ToHashSet();
        var parts = new List<string>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            var text = GetText(hwnd);
            if (hintPids.Contains((int)pid) || text.Contains("GSPro", StringComparison.OrdinalIgnoreCase) || text.Contains("Configuration", StringComparison.OrdinalIgnoreCase))
                parts.Add(Describe(hwnd) + " controls: " + DescribeChildren(hwnd));
            return parts.Count < 15;
        }, IntPtr.Zero);

        var running = string.Join(", ", processHints.Select(h => $"{h}={(ProcessTools.IsRunning(h) ? "running" : "not running")}"));
        return (parts.Count == 0 ? "(no matching windows)" : string.Join(" | ", parts)) + $" — processes: {running}; MySimCaddie admin: {(IsCurrentProcessElevated() ? "yes" : "no")}";
    }

    private static string DescribeChildren(IntPtr dialog)
    {
        var parts = new List<string>();
        Native.EnumChildWindows(dialog, (child, _) =>
        {
            var cls = new StringBuilder(128);
            Native.GetClassName(child, cls, cls.Capacity);
            parts.Add($"[{cls}] \"{GetText(child)}\"");
            return parts.Count < 30;
        }, IntPtr.Zero);
        return parts.Count == 0 ? "(none)" : string.Join(", ", parts);
    }

    private static string ProcessName(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return "?";
        }
    }

    private static string GetText(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        Native.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
