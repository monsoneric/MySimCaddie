using System.Runtime.InteropServices;
using MySimCaddie.Core.Config;
using MySimCaddie.Core.Platform;

namespace MySimCaddie.Core.Displays;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public override string ToString() => $"{Width}×{Height} @ ({X},{Y})";
}

/// <summary>A connected display. All coordinates are physical pixels (the app is per-monitor DPI aware).</summary>
public sealed record DisplayInfo(
    string GdiName,
    string FriendlyName,
    string DevicePath,
    PixelRect Bounds,
    PixelRect WorkArea,
    bool IsPrimary,
    IntPtr HMonitor)
{
    public int Number
    {
        get
        {
            // "\\.\DISPLAY3" → 3
            var digits = new string(GdiName.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var n) ? n : 0;
        }
    }

    public string DisplayName => string.IsNullOrWhiteSpace(FriendlyName) ? $"Display {Number}" : FriendlyName;

    public string Description => $"{DisplayName} — {Bounds.Width}×{Bounds.Height}{(IsPrimary ? " — primary" : "")}";

    public DisplayMatch ToMatch() => new() { FriendlyName = FriendlyName, DevicePath = DevicePath, GdiName = GdiName };
}

public static class DisplayService
{
    public static IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var names = QueryFriendlyNames();
        var list = new List<DisplayInfo>();

        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(hMon, ref mi))
            {
                names.TryGetValue(mi.szDevice, out var n);
                list.Add(new DisplayInfo(
                    mi.szDevice,
                    n.Friendly ?? "",
                    n.Path ?? "",
                    ToRect(mi.rcMonitor),
                    ToRect(mi.rcWork),
                    (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0,
                    hMon));
            }

            return true;
        }, IntPtr.Zero);

        return list.OrderBy(d => d.Bounds.X).ThenBy(d => d.Bounds.Y).ToList();
    }

    /// <summary>Find the display for a role, or null if it isn't assigned / isn't connected.</summary>
    public static DisplayInfo? Resolve(AppConfig cfg, string role, IReadOnlyList<DisplayInfo>? displays = null)
    {
        if (string.IsNullOrWhiteSpace(role) || !cfg.Displays.TryGetValue(role, out var match) || match.IsEmpty)
            return null;
        return Resolve(match, displays ?? GetDisplays());
    }

    public static DisplayInfo? Resolve(DisplayMatch match, IReadOnlyList<DisplayInfo> displays)
    {
        if (!string.IsNullOrWhiteSpace(match.DevicePath))
        {
            var byPath = displays.FirstOrDefault(d => d.DevicePath.Equals(match.DevicePath, StringComparison.OrdinalIgnoreCase));
            if (byPath is not null) return byPath;
        }

        if (!string.IsNullOrWhiteSpace(match.FriendlyName))
        {
            var byName = displays.Where(d => d.FriendlyName.Equals(match.FriendlyName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 1) return byName[0];
        }

        if (!string.IsNullOrWhiteSpace(match.GdiName))
            return displays.FirstOrDefault(d => d.GdiName.Equals(match.GdiName, StringComparison.OrdinalIgnoreCase));

        return null;
    }

    /// <summary>
    /// First-run guess at which display is which, from the monitor names Windows reports
    /// (e.g. "BenQ AH500ST"). The Setup screen lets you correct it.
    /// </summary>
    public static Dictionary<string, DisplayMatch> GuessRoles(IReadOnlyList<DisplayInfo> displays)
    {
        string[] projectorHints = { "benq", "ah500", "projector", "epson", "optoma", "xgimi", "anker", "nebula" };
        string[] tvHints = { "tv", "samsung", "vizio", "tcl", "hisense", "sony", "insignia", "roku", "toshiba", "sharp", "lg tv" };

        var remaining = displays.ToList();
        var result = new Dictionary<string, DisplayMatch>(StringComparer.OrdinalIgnoreCase);

        DisplayInfo? Take(string[] hints)
        {
            var d = remaining.FirstOrDefault(x => hints.Any(h => x.FriendlyName.Contains(h, StringComparison.OrdinalIgnoreCase)));
            if (d is not null) remaining.Remove(d);
            return d;
        }

        var projector = Take(projectorHints);
        var tv = Take(tvHints);

        // The monitor is usually the smallest-resolution or the remaining one.
        var monitor = remaining.OrderBy(d => d.Bounds.Width * d.Bounds.Height).FirstOrDefault();
        if (monitor is not null) remaining.Remove(monitor);
        projector ??= remaining.FirstOrDefault(d => d.IsPrimary) ?? remaining.FirstOrDefault();
        if (projector is not null) remaining.Remove(projector);
        tv ??= remaining.FirstOrDefault();

        if (projector is not null) result[DisplayRoles.Projector] = projector.ToMatch();
        if (tv is not null) result[DisplayRoles.Tv] = tv.ToMatch();
        if (monitor is not null) result[DisplayRoles.Monitor] = monitor.ToMatch();
        return result;
    }

    /// <summary>
    /// Make <paramref name="target"/> the Windows primary display. Windows defines the primary as the display
    /// at (0,0), so every display is shifted by the same offset to keep the arrangement intact.
    /// </summary>
    public static bool SetPrimary(DisplayInfo target)
    {
        if (target.IsPrimary) return true;

        var displays = GetDisplays();
        int dx = -target.Bounds.X, dy = -target.Bounds.Y;
        Log.Info($"Setting primary display → {target.Description}");

        foreach (var d in displays)
        {
            var dm = new Native.DEVMODE
            {
                dmDeviceName = new string('\0', 32),
                dmFormName = new string('\0', 32),
                dmSize = (short)Marshal.SizeOf<Native.DEVMODE>(),
            };
            if (!Native.EnumDisplaySettings(d.GdiName, Native.ENUM_CURRENT_SETTINGS, ref dm))
            {
                Log.Warn($"EnumDisplaySettings failed for {d.GdiName}");
                return false;
            }

            bool isTarget = d.GdiName.Equals(target.GdiName, StringComparison.OrdinalIgnoreCase);
            dm.dmPositionX = isTarget ? 0 : d.Bounds.X + dx;
            dm.dmPositionY = isTarget ? 0 : d.Bounds.Y + dy;
            dm.dmFields = Native.DM_POSITION;

            uint flags = Native.CDS_UPDATEREGISTRY | Native.CDS_NORESET | (isTarget ? Native.CDS_SET_PRIMARY : 0);
            int rc = Native.ChangeDisplaySettingsEx(d.GdiName, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
            if (rc != Native.DISP_CHANGE_SUCCESSFUL)
            {
                Log.Warn($"ChangeDisplaySettingsEx({d.GdiName}) returned {rc}");
                return false;
            }
        }

        // Apply all staged changes at once.
        int apply = Native.ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        if (apply != Native.DISP_CHANGE_SUCCESSFUL)
        {
            Log.Warn($"Applying display changes returned {apply}");
            return false;
        }

        return true;
    }

    public static DisplayInfo? GetPrimary() => GetDisplays().FirstOrDefault(d => d.IsPrimary);

    private static PixelRect ToRect(Native.RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    /// <summary>GDI name ("\\.\DISPLAY2") → (EDID friendly name, stable device path).</summary>
    private static Dictionary<string, (string? Friendly, string? Path)> QueryFriendlyNames()
    {
        var map = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0)
                return map;

            var paths = new Native.DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new Native.DISPLAYCONFIG_MODE_INFO[modeCount];
            if (Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                return map;

            for (int i = 0; i < pathCount; i++)
            {
                var p = paths[i];

                var source = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = Marshal.SizeOf<Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = p.sourceInfo.adapterId,
                        id = p.sourceInfo.id,
                    },
                };
                if (Native.DisplayConfigGetDeviceInfo(ref source) != 0) continue;

                var target = new Native.DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                        size = Marshal.SizeOf<Native.DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = p.targetInfo.adapterId,
                        id = p.targetInfo.id,
                    },
                };
                if (Native.DisplayConfigGetDeviceInfo(ref target) != 0) continue;

                map[source.viewGdiDeviceName] = (target.monitorFriendlyDeviceName, target.monitorDevicePath);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not read monitor names", ex);
        }

        return map;
    }
}
