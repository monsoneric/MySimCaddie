using System.Text.Json.Serialization;

namespace MySimCaddie.Core.Config;

/// <summary>Root of config.json. Everything the launcher does is driven from here.</summary>
public sealed class AppConfig
{
    public int Version { get; set; } = 1;

    public string RoomName { get; set; } = "JuiceBoxGuy's Hack Shack";

    /// <summary>Absolute path to the background logo (PNG/JPG). Empty = text-only header.</summary>
    public string LogoPath { get; set; } = "";

    /// <summary>0..1 opacity of the logo behind the tiles.</summary>
    public double BackgroundLogoOpacity { get; set; } = 0.22;

    public string AccentColor { get; set; } = "#16A34A";

    /// <summary>Where the tiles sit on the home screen: "Left", "Right" or "Top". The logo gets the rest.</summary>
    public string TileLayout { get; set; } = TileLayouts.Left;

    /// <summary>Which display role the launcher itself lives on.</summary>
    public string LauncherDisplay { get; set; } = DisplayRoles.Tv;

    /// <summary>Make this role the Windows primary display when the launcher starts (empty = leave alone).</summary>
    public string PrimaryDisplayAtStartup { get; set; } = DisplayRoles.Projector;

    public bool StartWithWindows { get; set; } = true;

    /// <summary>
    /// Run MySimCaddie as administrator (via a Task Scheduler task, so no prompt at sign-in). Needed to press
    /// buttons in programs that run as administrator, such as GSPro when its "Run as admin" is on.
    /// </summary>
    public bool RunElevated { get; set; }

    /// <summary>Bookkeeping: what the administrator startup task was last registered for ("exe|atSignIn").</summary>
    public string RegisteredTask { get; set; } = "";

    /// <summary>Run this profile automatically when the launcher starts (empty = none).</summary>
    public string AutoRunProfile { get; set; } = "";

    /// <summary>Role name ("Projector", "TV", "Monitor") → which physical display it is.</summary>
    public Dictionary<string, DisplayMatch> Displays { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>App id → app definition. Profiles reference apps by id.</summary>
    public Dictionary<string, AppDefinition> Apps { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<Profile> Profiles { get; set; } = new();

    public List<QuickLink> QuickLinks { get; set; } = new();
}

public static class TileLayouts
{
    public const string Left = "Left";
    public const string Right = "Right";
    public const string Top = "Top";
}

public static class DisplayRoles
{
    public const string Projector = "Projector";
    public const string Tv = "TV";
    public const string Monitor = "Monitor";

    public static readonly string[] All = { Projector, Tv, Monitor };
}

/// <summary>Identifies a physical display. Matched by device path, then friendly name, then GDI name.</summary>
public sealed class DisplayMatch
{
    public string FriendlyName { get; set; } = "";
    public string DevicePath { get; set; } = "";
    public string GdiName { get; set; } = "";

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(FriendlyName) && string.IsNullOrWhiteSpace(DevicePath) && string.IsNullOrWhiteSpace(GdiName);
}

public sealed class AppDefinition
{
    public string Name { get; set; } = "";

    /// <summary>Exe path. Environment variables (e.g. %LOCALAPPDATA%) are expanded.</summary>
    public string Path { get; set; } = "";

    public string Arguments { get; set; } = "";

    /// <summary>Process name used to detect it running (no .exe). Defaults to the exe file name.</summary>
    public string ProcessName { get; set; } = "";

    public bool RunAsAdmin { get; set; }

    /// <summary>Segoe Fluent Icons code point in hex, e.g. "E7C1".</summary>
    public string Icon { get; set; } = "E768";

    /// <summary>Show a button for this app in the quick-launch row.</summary>
    public bool ShowInQuickLaunch { get; set; } = true;

    /// <summary>Show a running/idle chip for this app in the status bar.</summary>
    public bool ShowStatus { get; set; } = true;

    [JsonIgnore]
    public string ExpandedPath => Environment.ExpandEnvironmentVariables(Path ?? "");

    [JsonIgnore]
    public string EffectiveProcessName =>
        !string.IsNullOrWhiteSpace(ProcessName)
            ? ProcessName.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase)
            : System.IO.Path.GetFileNameWithoutExtension(ExpandedPath);
}

public sealed class Profile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Icon { get; set; } = "E7C1";

    /// <summary>Optional per-tile accent; falls back to the global accent.</summary>
    public string AccentColor { get; set; } = "";

    /// <summary>Role to make primary before launching (games go fullscreen on the primary display).</summary>
    public string PrimaryDisplay { get; set; } = "";

    /// <summary>Put the previous primary display back when the session ends.</summary>
    public bool RestorePrimaryOnEnd { get; set; }

    public List<LaunchStep> Steps { get; set; } = new();

    /// <summary>
    /// The process whose lifetime defines the session (e.g. "GSPro"). When it closes the session ends
    /// and companions are cleaned up. Empty = session ends once all steps have run.
    /// </summary>
    public string SessionProcess { get; set; } = "";

    /// <summary>How long to wait for the session process to appear (you may need to click Play in a launcher).</summary>
    public int SessionStartTimeoutSeconds { get; set; } = 600;

    /// <summary>Extra app ids to close when the session ends (in addition to steps marked CloseOnEnd).</summary>
    public List<string> AlsoCloseOnEnd { get; set; } = new();
}

public enum IfRunningBehavior
{
    /// <summary>Leave the running copy alone (still places its window).</summary>
    Skip,
    /// <summary>Close it and start fresh.</summary>
    Restart,
}

public enum WindowMode
{
    /// <summary>Don't touch the window.</summary>
    None,
    /// <summary>Move to the target display and maximize.</summary>
    Maximize,
    /// <summary>Move to the target display and size to cover it exactly (borderless-style).</summary>
    Fill,
    /// <summary>Move to the target display, keeping the window's size.</summary>
    Move,
}

public sealed class LaunchStep
{
    /// <summary>App id from <see cref="AppConfig.Apps"/>.</summary>
    public string App { get; set; } = "";

    public IfRunningBehavior IfRunning { get; set; } = IfRunningBehavior.Skip;

    /// <summary>
    /// Process to wait for after starting (for launchers that hand off to a different exe,
    /// e.g. GSPLauncher → GSPro). Empty = the app's own process.
    /// </summary>
    public string WaitForProcess { get; set; } = "";

    public int WaitTimeoutSeconds { get; set; } = 60;

    /// <summary>Don't block the next step while waiting for WaitForProcess (it's picked up by the session wait).</summary>
    public bool WaitInBackground { get; set; }

    /// <summary>Display role to put the window on (empty = leave it).</summary>
    public string Display { get; set; } = "";

    public WindowMode Window { get; set; } = WindowMode.None;

    public int DelayAfterSeconds { get; set; }

    public bool CloseOnEnd { get; set; }

    /// <summary>
    /// Press a button in a pop-up automatically, e.g. "Play!" in the "GSPro Configuration" window.
    /// Matches any visible window whose title contains this text (empty = off).
    /// </summary>
    public string AutoClickWindow { get; set; } = "";

    public string AutoClickButton { get; set; } = "";

    public int AutoClickTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// A second button to press if it shows up, e.g. "Connect" in GSPro Connect's Connection Manager.
    /// Optional: if the button never appears (the device connected by itself), nothing happens.
    /// </summary>
    public string AutoClick2Window { get; set; } = "";

    public string AutoClick2Button { get; set; } = "";
}

public static class GsproPreset
{
    public const string Window = "GSPro Configuration";
    public const string Button = "Play!";

    /// <summary>GSPro Connect's main window ("GSPro x Foresight Sports &amp; Bushnell Golf v1.14").</summary>
    public const string ConnectWindow = "GSPro x Foresight";
    public const string ConnectButton = "Connect";

    public static bool IsGspro(string? exePath)
    {
        var name = System.IO.Path.GetFileName(Environment.ExpandEnvironmentVariables(exePath ?? ""));
        return name.Equals("GSPLauncher.exe", StringComparison.OrdinalIgnoreCase)
               || name.Equals("GSPro.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Fill in GSPro's auto-press + hand-off defaults on a step if they're empty.</summary>
    public static void Apply(LaunchStep step)
    {
        if (string.IsNullOrWhiteSpace(step.AutoClickWindow)) step.AutoClickWindow = Window;
        if (string.IsNullOrWhiteSpace(step.AutoClickButton)) step.AutoClickButton = Button;
        ApplyConnect(step);
    }

    /// <summary>Also press Connect in GSPro Connect when it stops on the Connection Manager tab.</summary>
    public static void ApplyConnect(LaunchStep step)
    {
        if (string.IsNullOrWhiteSpace(step.AutoClick2Window)) step.AutoClick2Window = ConnectWindow;
        if (string.IsNullOrWhiteSpace(step.AutoClick2Button)) step.AutoClick2Button = ConnectButton;
    }
}

public sealed class QuickLink
{
    public string Name { get; set; } = "";

    /// <summary>A folder, file or URL. Environment variables are expanded.</summary>
    public string Target { get; set; } = "";

    public string Icon { get; set; } = "E8B7";
}
