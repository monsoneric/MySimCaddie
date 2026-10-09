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
    /// <summary>The button was pressed but stayed on screen (e.g. Connect couldn't find the device).</summary>
    StillShowing,
    /// <summary>The button was there but the window never became ready (e.g. no device in GSPro Connect's list).</summary>
    NeverReady,
}

public sealed record AutoClickOptions
{
    public static readonly AutoClickOptions Default = new();

    /// <summary>How long the button must stay on screen before it's pressed.</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromMilliseconds(800);

    /// <summary>Press only if the button shows up; don't treat "never appeared" as a problem.</summary>
    public bool Optional { get; init; }

    /// <summary>Give up after this many presses that didn't make the button go away.</summary>
    public int MaxAttempts { get; init; } = 6;

    /// <summary>
    /// Don't press until every drop-down list in the window has something selected, e.g. GSPro Connect's device list
    /// is empty for a few seconds while it looks for the launch monitor, and Connect then only complains.
    /// </summary>
    public bool WaitForSelection { get; init; }

    /// <summary>
    /// Status-label mode for connect buttons: a label in the window starting with this text ends in a count, e.g.
    /// GSPro Connect's "Connected Devices: 0". The button is pressed only while the count is 0 (even if its tab isn't
    /// showing), and it has worked once the count goes above 0.
    /// </summary>
    public string? StatusLabelPrefix { get; init; }
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

    /// <summary>UI Automation tab selection by name (set by the app).</summary>
    public static Func<IntPtr, string, bool>? SelectTab { get; set; }

    /// <summary>"&amp;Play!" → "play"</summary>
    public static string Normalize(string? text) =>
        (text ?? "").Replace("&", "").Trim().TrimEnd('!', '.', '…').Trim().ToLowerInvariant();

    public static async Task<AutoClickResult> ClickAsync(string windowTitle, string buttonText, IEnumerable<string> processHints,
        TimeSpan timeout, CancellationToken ct, AutoClickOptions? options = null)
    {
        options ??= AutoClickOptions.Default;
        if (!string.IsNullOrWhiteSpace(options.StatusLabelPrefix))
            return await ConnectAsync(windowTitle, buttonText, processHints, timeout, ct, options);

        var hints = processHints.Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var started = DateTime.UtcNow;
        var deadline = started + timeout;
        int attempt = 0;
        bool loggedControls = false, loggedScan = false, loggedWaiting = false, loggedNotReady = false, sawNotReady = false;
        IntPtr lastDialog = IntPtr.Zero;
        DateTime? buttonSince = null;
        int complaints = 0;

        Log.Info($"Auto-press: watching for \"{buttonText}\" in a window titled \"{windowTitle}\" (or any {string.Join("/", hints)} window with that button)"
                 + (options.Optional ? " — only if it shows up" : ""));

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
                    buttonSince = null;
                    Log.Info($"Auto-press: found {Describe(dialog)}");

                    if (IsBlockedByElevation(dialog))
                    {
                        Log.Warn("Auto-press: that window belongs to a program running as administrator and MySimCaddie isn't, so Windows blocks the click");
                        return AutoClickResult.Blocked;
                    }
                }

                // The button has to stay on screen for a moment first: it lets pop-ups finish drawing, and gives
                // programs like GSPro Connect time to connect by themselves (the Connect button then disappears).
                if (FindChildButton(dialog, buttonText) == IntPtr.Zero)
                {
                    buttonSince = null;
                    sawNotReady = false; // e.g. it connected by itself
                    if (options.Optional)
                    {
                        // e.g. GSPro Connect already connected and switched away from the Connect tab: nothing to do.
                        await Task.Delay(500, ct);
                        continue;
                    }
                }
                else if (options.WaitForSelection && !HasSelections(dialog))
                {
                    buttonSince = null;
                    sawNotReady = true;
                    if (!loggedNotReady)
                    {
                        loggedNotReady = true;
                        Log.Info($"Auto-press: \"{buttonText}\" is showing but a drop-down list is still empty; waiting for it");
                    }

                    await Task.Delay(500, ct);
                    continue;
                }
                else
                {
                    sawNotReady = false;
                    buttonSince ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - buttonSince < options.Settle)
                    {
                        if (!loggedWaiting && options.Settle > TimeSpan.FromSeconds(1))
                        {
                            loggedWaiting = true;
                            Log.Info($"Auto-press: \"{buttonText}\" is showing; giving it {options.Settle.TotalSeconds:0}s before pressing");
                        }

                        await Task.Delay(250, ct);
                        continue;
                    }
                }

                if (TryClick(dialog, buttonText, attempt++, out bool accessDenied))
                {
                    bool complained = false;
                    for (int i = 0; i < 16 && !complained; i++)
                    {
                        await Task.Delay(250, ct);
                        if (DismissComplaint(dialog))
                        {
                            complained = true;
                            break;
                        }

                        if (!Native.IsWindow(dialog) || !Native.IsWindowVisible(dialog) || FindChildButton(dialog, buttonText) == IntPtr.Zero)
                        {
                            Log.Info($"Auto-press: pressed \"{buttonText}\" (method {attempt})");
                            return AutoClickResult.Clicked;
                        }
                    }

                    if (complained)
                    {
                        // The program said it wasn't ready. Same method again once it has settled again.
                        attempt--;
                        buttonSince = null;
                        if (++complaints >= options.MaxAttempts)
                        {
                            Log.Warn($"Auto-press: \"{buttonText}\" was refused {complaints} times; giving up");
                            return AutoClickResult.StillShowing;
                        }

                        continue;
                    }

                    if (attempt >= options.MaxAttempts)
                    {
                        Log.Warn($"Auto-press: pressed \"{buttonText}\" {attempt} times but it's still showing; giving up");
                        return AutoClickResult.StillShowing;
                    }

                    Log.Warn($"Auto-press: \"{buttonText}\" still showing after method {attempt}; trying another way");
                }
                else if (accessDenied)
                {
                    Log.Warn("Auto-press: Windows refused the click (access denied — the program is probably running as administrator)");
                    return AutoClickResult.Blocked;
                }
                else if (!loggedControls && !options.Optional)
                {
                    loggedControls = true;
                    Log.Warn($"Auto-press: no \"{buttonText}\" button found. Controls: {DescribeChildren(dialog)}");
                }
            }
            else if (!loggedScan && !options.Optional && DateTime.UtcNow - started > TimeSpan.FromSeconds(20))
            {
                loggedScan = true;
                Log.Warn($"Auto-press: nothing matched after 20s. Windows on screen: {ScanForDiagnostics(hints)}");
            }

            await Task.Delay(500, ct);
        }

        if (sawNotReady)
        {
            Log.Warn($"Auto-press: \"{buttonText}\" was showing but the window never became ready; not pressed");
            return AutoClickResult.NeverReady;
        }

        if (options.Optional)
            Log.Info($"Auto-press: \"{buttonText}\" never needed pressing");
        else if (!loggedScan)
            Log.Warn($"Auto-press: gave up. Windows on screen: {ScanForDiagnostics(hints)}");
        return AutoClickResult.NotFound;
    }

    /// <summary>
    /// Status-label mode (see <see cref="AutoClickOptions.StatusLabelPrefix"/>), for GSPro Connect: it can flip to its
    /// Shot Data tab without connecting, so the button being hidden says nothing. The count label does.
    /// </summary>
    private static async Task<AutoClickResult> ConnectAsync(string windowTitle, string buttonText, IEnumerable<string> processHints,
        TimeSpan timeout, CancellationToken ct, AutoClickOptions options)
    {
        const string searchText = "Search";
        var prefix = options.StatusLabelPrefix!;
        var deadline = DateTime.UtcNow + timeout;
        IntPtr lastWindow = IntPtr.Zero;
        DateTime? selectedSince = null, unselectedSince = null, lastSearch = null, lastConnect = null;
        int connects = 0, searches = 0, tabTries = 0;
        DateTime? lastTabTry = null;
        string lastState = "";

        Log.Info($"Auto-press: watching \"{windowTitle}\" — will press {searchText} and \"{buttonText}\" if \"{prefix}\" stays at 0");

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var window = FindWindowByTitle(windowTitle);
            if (window == IntPtr.Zero)
            {
                await Task.Delay(500, ct);
                continue;
            }

            if (window != lastWindow)
            {
                lastWindow = window;
                selectedSince = unselectedSince = null;
                Log.Info($"Auto-press: found {Describe(window)} controls: {DescribeChildren(window, remote: true)}");
                if (IsBlockedByElevation(window))
                {
                    Log.Warn("Auto-press: that window belongs to a program running as administrator and MySimCaddie isn't, so Windows blocks the click");
                    return AutoClickResult.Blocked;
                }
            }

            DismissComplaint(window);

            var count = ReadCount(window, prefix);
            var connect = FindChildButton(window, buttonText, requireVisible: false);
            var search = FindChildButton(window, searchText, requireVisible: false);

            // Windows Forms only builds a tab's controls the first time it's shown. GSPro Connect sometimes opens
            // on Shot Data, so the Connection Manager tab (Search, the device list, the count) doesn't exist yet.
            if ((count is null || search == IntPtr.Zero) && tabTries < 4 && (lastTabTry is null || DateTime.UtcNow - lastTabTry >= TimeSpan.FromSeconds(3)))
            {
                tabTries++;
                lastTabTry = DateTime.UtcNow;
                OpenTab(window, ConnectionTab, tabTries);
                await Task.Delay(1000, ct);
                Log.Info($"Auto-press: after opening the {ConnectionTab} tab: {DescribeChildren(window, remote: true, onlyVisible: true)}");
                continue;
            }
            bool selected = connect != IntPtr.Zero && SiblingListsHaveSelection(connect);
            var state = $"count={(count?.ToString() ?? "?")}, connect={(connect != IntPtr.Zero ? "yes" : "no")}, search={(search != IntPtr.Zero ? "yes" : "no")}, device selected={selected}";
            if (state != lastState)
            {
                lastState = state;
                Log.Info($"Auto-press: {windowTitle}: {state}");
            }

            if (count > 0)
            {
                Log.Info(connects == 0 && searches == 0
                    ? $"Auto-press: connected by itself — nothing to press"
                    : $"Auto-press: connected ({searches} search, {connects} connect)");
                return connects == 0 && searches == 0 ? AutoClickResult.NotFound : AutoClickResult.Clicked;
            }

            if (connect == IntPtr.Zero)
            {
                await Task.Delay(500, ct);
                continue;
            }

            var now = DateTime.UtcNow;
            if (selected)
            {
                unselectedSince = null;
                selectedSince ??= now;

                // GSPro Connect tries the device itself as soon as it's listed; give that a chance, unless we searched.
                var wait = lastSearch is not null ? TimeSpan.FromSeconds(3) : options.Settle;
                bool connectDue = now - selectedSince >= wait && (lastConnect is null || now - lastConnect >= TimeSpan.FromSeconds(15));
                if (connectDue)
                {
                    if (connects >= options.MaxAttempts)
                    {
                        Log.Warn($"Auto-press: pressed \"{buttonText}\" {connects} times and it still isn't connected; giving up");
                        return AutoClickResult.StillShowing;
                    }

                    connects++;
                    lastConnect = now;
                    Press(window, connect);
                    Log.Info($"Auto-press: pressed \"{buttonText}\" (try {connects})");

                    if (count is null)
                    {
                        // Can't tell whether it worked, so don't risk pressing Connect on a live connection.
                        Log.Warn($"Auto-press: couldn't read \"{prefix}\", so pressing \"{buttonText}\" only once");
                        return AutoClickResult.Clicked;
                    }
                }
            }
            else
            {
                selectedSince = null;
                unselectedSince ??= now;

                // Nothing in the list (its own try failed, or it's still looking): press Search, then Connect once it's listed.
                bool searchDue = search != IntPtr.Zero && now - unselectedSince >= TimeSpan.FromSeconds(4)
                                 && (lastSearch is null || now - lastSearch >= TimeSpan.FromSeconds(15));
                if (searchDue)
                {
                    if (searches >= options.MaxAttempts + 1)
                    {
                        Log.Warn($"Auto-press: pressed {searchText} {searches} times and no device showed up; giving up");
                        return AutoClickResult.NeverReady;
                    }

                    searches++;
                    lastSearch = now;
                    Press(window, search);
                    Log.Info($"Auto-press: pressed {searchText} (try {searches})");
                }
            }

            await Task.Delay(500, ct);
        }

        if (connects > 0)
        {
            Log.Warn($"Auto-press: still not connected after pressing \"{buttonText}\" {connects} times");
            return AutoClickResult.StillShowing;
        }

        if (searches > 0)
        {
            Log.Warn($"Auto-press: {windowTitle} never listed a device to connect to");
            return AutoClickResult.NeverReady;
        }

        Log.Info($"Auto-press: \"{buttonText}\" never needed pressing");
        return AutoClickResult.NotFound;
    }

    private const string ConnectionTab = "Connection Manager";

    /// <summary>Shows a tab: by name through UI Automation, else by telling the tab control to move to the second tab.</summary>
    private static void OpenTab(IntPtr window, string name, int attempt)
    {
        try
        {
            if (attempt % 2 == 1 && SelectTab?.Invoke(window, name) == true) return;
        }
        catch (Exception ex)
        {
            Log.Error("Selecting a tab via UI Automation failed", ex);
        }

        // TCM_SETCURFOCUS (unlike TCM_SETCURSEL) sends the change notifications Windows Forms listens for.
        IntPtr tab = IntPtr.Zero;
        Native.EnumChildWindows(window, (child, _) =>
        {
            var cls = new StringBuilder(128);
            Native.GetClassName(child, cls, cls.Capacity);
            if (!cls.ToString().Contains("SysTabControl32", StringComparison.OrdinalIgnoreCase)) return true;
            tab = child;
            return false;
        }, IntPtr.Zero);

        if (tab == IntPtr.Zero)
        {
            Log.Warn("Auto-press: no tab control found");
            return;
        }

        Native.SendMessageTimeout(tab, Native.TCM_SETCURFOCUS, (IntPtr)1, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 1000, out _);
        Native.SendMessageTimeout(tab, Native.TCM_GETCURSEL, IntPtr.Zero, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 500, out var cur);
        Log.Info($"Auto-press: asked the tab control for tab 2 (now on tab {cur.ToInt64() + 1})");
    }

    /// <summary>Sends the button's click message to its window, which works even when its tab isn't showing.</summary>
    private static void Press(IntPtr window, IntPtr button)
    {
        int id = Native.GetDlgCtrlID(button);
        var wParam = (IntPtr)((Native.BN_CLICKED << 16) | (id & 0xFFFF));
        Native.PostMessage(window, Native.WM_COMMAND, wParam, button);
    }

    private static IntPtr FindWindowByTitle(string titleContains)
    {
        int self = Environment.ProcessId;
        var title = (titleContains ?? "").Trim();
        if (title.Length == 0) return IntPtr.Zero;
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == self) return true;
            if (!GetText(hwnd).Contains(title, StringComparison.OrdinalIgnoreCase)) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>"Connected Devices: 2" → 2. Null if there's no such label.</summary>
    private static int? ReadCount(IntPtr window, string prefix)
    {
        int? count = null;
        Native.EnumChildWindows(window, (child, _) =>
        {
            var text = GetRemoteText(child).Trim();
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            var digits = new string(text[prefix.Length..].Where(char.IsDigit).ToArray());
            count = int.TryParse(digits, out var n) ? n : 0;
            return false;
        }, IntPtr.Zero);
        return count;
    }

    /// <summary>Drop-down lists next to the button (same parent) all have something selected.</summary>
    private static bool SiblingListsHaveSelection(IntPtr button)
    {
        var parent = Native.GetParent(button);
        if (parent == IntPtr.Zero) return true;
        bool ok = true, any = false;
        Native.EnumChildWindows(parent, (child, _) =>
        {
            if (Native.GetParent(child) != parent) return true;
            var cls = new StringBuilder(128);
            Native.GetClassName(child, cls, cls.Capacity);
            if (!cls.ToString().Contains("COMBOBOX", StringComparison.OrdinalIgnoreCase)) return true;
            any = true;
            if (Native.SendMessageTimeout(child, Native.CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 500, out var sel) != IntPtr.Zero
                && sel.ToInt64() < 0)
            {
                ok = false;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return ok || !any;
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

    private static IntPtr FindChildButton(IntPtr dialog, string buttonText, bool requireVisible = true)
    {
        var want = Normalize(buttonText);
        IntPtr found = IntPtr.Zero;
        Native.EnumChildWindows(dialog, (child, _) =>
        {
            if ((!requireVisible || Native.IsWindowVisible(child)) && IsButton(child)
                && (Normalize(GetText(child)) == want || Normalize(GetRemoteText(child)) == want))
            {
                found = child;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>True when no visible drop-down list in the window is empty or has nothing selected.</summary>
    private static bool HasSelections(IntPtr dialog)
    {
        bool ok = true;
        Native.EnumChildWindows(dialog, (child, _) =>
        {
            if (!Native.IsWindowVisible(child)) return true;
            var cls = new StringBuilder(128);
            Native.GetClassName(child, cls, cls.Capacity);
            if (!cls.ToString().Contains("COMBOBOX", StringComparison.OrdinalIgnoreCase)) return true;

            // Window text isn't readable across programs for drop-downs, but the selected index is.
            if (Native.SendMessageTimeout(child, Native.CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 500, out var sel) != IntPtr.Zero
                && sel.ToInt64() < 0)
            {
                ok = false;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return ok;
    }

    /// <summary>
    /// If the program answered the press with a message box (e.g. "You have to select a port in the drop down list"),
    /// log it, press its OK and return true.
    /// </summary>
    private static bool DismissComplaint(IntPtr dialog)
    {
        Native.GetWindowThreadProcessId(dialog, out var pid);
        bool found = false;
        Native.EnumWindows((hwnd, _) =>
        {
            if (hwnd == dialog || !Native.IsWindowVisible(hwnd)) return true;
            Native.GetWindowThreadProcessId(hwnd, out var p);
            if (p != pid) return true;
            var cls = new StringBuilder(64);
            Native.GetClassName(hwnd, cls, cls.Capacity);
            if (cls.ToString() != "#32770") return true;

            var ok = FindChildButton(hwnd, "OK");
            if (ok == IntPtr.Zero) return true;

            Log.Warn($"Auto-press: the program answered with a message: {DescribeChildren(hwnd)} — closing it");
            Native.PostMessage(ok, Native.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            found = true;
            return true; // close every one of them
        }, IntPtr.Zero);
        return found;
    }

    private static bool IsButton(IntPtr hwnd)
    {
        var cls = new StringBuilder(128);
        Native.GetClassName(hwnd, cls, cls.Capacity);
        return cls.ToString().Contains("BUTTON", StringComparison.OrdinalIgnoreCase);
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

    private static string DescribeChildren(IntPtr dialog, bool remote = false, bool onlyVisible = false)
    {
        var parts = new List<string>();
        Native.EnumChildWindows(dialog, (child, _) =>
        {
            var cls = new StringBuilder(128);
            Native.GetClassName(child, cls, cls.Capacity);
            if (onlyVisible && !Native.IsWindowVisible(child)) return true;
            var text = remote ? GetRemoteText(child) : GetText(child);
            parts.Add($"[{cls}] \"{text}\"{(Native.IsWindowVisible(child) ? "" : " (hidden)")}");
            return parts.Count < (remote ? 120 : 40);
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

    /// <summary>
    /// A control's text as the program sees it. GetWindowText can't read many controls in other programs
    /// (it only returns a cached caption), but WM_GETTEXT is passed through to them.
    /// </summary>
    private static string GetRemoteText(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        return Native.SendMessageTimeout(hwnd, Native.WM_GETTEXT, (IntPtr)sb.Capacity, sb, Native.SMTO_ABORTIFHUNG, 500, out _) != IntPtr.Zero
            ? sb.ToString()
            : GetText(hwnd);
    }

    private static string GetText(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        Native.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
