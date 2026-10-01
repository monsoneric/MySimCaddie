using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using MySimCaddie.Core;
using MySimCaddie.Core.Config;
using MySimCaddie.Core.Displays;
using MySimCaddie.Core.Launch;

namespace MySimCaddie.Views;

public partial class SetupView : UserControl
{
    private AppConfig? _cfg;
    private SetupVm? _vm;

    /// <summary>true = saved (config already updated in place), false = cancelled.</summary>
    public event Action<bool>? Finished;

    public event Action? IdentifyRequested;

    public SetupView() => InitializeComponent();

    public void Open(AppConfig cfg)
    {
        _cfg = cfg;
        _vm = SetupVm.From(cfg, DisplayService.GetDisplays());
        // Only the inner root: the control's own DataContext must stay MainVm so its Visibility binding keeps working.
        Root.DataContext = _vm;
    }

    public void Cancel() => Finished?.Invoke(false);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_cfg is null || _vm is null) return;
        _vm.ApplyTo(_cfg);
        Finished?.Invoke(true);
    }

    private void Identify_Click(object sender, RoutedEventArgs e) => IdentifyRequested?.Invoke();

    private void BrowseApp_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AppRowVm row) return;

        var dlg = new OpenFileDialog
        {
            Title = $"Find {row.Name}",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
        };
        var current = Environment.ExpandEnvironmentVariables(row.Path ?? "");
        if (File.Exists(current)) dlg.InitialDirectory = Path.GetDirectoryName(current);
        else if (Directory.Exists(@"C:\Program Files")) dlg.InitialDirectory = @"C:\Program Files";

        if (dlg.ShowDialog(Window.GetWindow(this)) == true) row.Path = dlg.FileName;
    }

    private void BrowseLogo_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Choose your room logo",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) _vm.LogoPath = dlg.FileName;
    }

    private void ClearLogo_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.LogoPath = "";
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e) => AppLauncher.Open(ConfigStore.DataDirectory);

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Log.LogDirectory);
        AppLauncher.Open(Log.LogDirectory);
    }
}

// ───────────── Setup view models ─────────────

public sealed class DisplayOption
{
    public DisplayInfo? Display { get; init; }
    public required string Text { get; init; }
}

public sealed class RoleRowVm : Observable
{
    public required string Role { get; init; }
    public required string Hint { get; init; }
    public required List<DisplayOption> Options { get; init; }

    private DisplayOption? _selected;
    public DisplayOption? Selected { get => _selected; set => Set(ref _selected, value); }
}

public sealed class AppRowVm : Observable
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    private string _path = "";
    public string Path
    {
        get => _path;
        set
        {
            if (Set(ref _path, value))
            {
                Raise(nameof(Status));
                Raise(nameof(StatusBrush));
            }
        }
    }

    private bool _runAsAdmin;
    public bool RunAsAdmin { get => _runAsAdmin; set => Set(ref _runAsAdmin, value); }

    private bool Found => !string.IsNullOrWhiteSpace(Path) && File.Exists(Environment.ExpandEnvironmentVariables(Path));

    public string Status => string.IsNullOrWhiteSpace(Path) ? "Not set" : Found ? "✓ Found" : "✗ Not found";

    public Brush StatusBrush => (Brush)Application.Current.Resources[Found ? "OkBrush" : "WarnBrush"];
}

public sealed class SetupVm : Observable
{
    public ObservableCollection<RoleRowVm> Roles { get; } = new();
    public ObservableCollection<AppRowVm> Apps { get; } = new();

    private string _roomName = "";
    public string RoomName { get => _roomName; set => Set(ref _roomName, value); }

    private string _logoPath = "";
    public string LogoPath { get => _logoPath; set => Set(ref _logoPath, value); }

    private double _logoOpacity;
    public double LogoOpacity { get => _logoOpacity; set => Set(ref _logoOpacity, Math.Round(value, 2)); }

    private bool _startWithWindows;
    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }

    private bool _projectorPrimaryAtStartup;
    public bool ProjectorPrimaryAtStartup { get => _projectorPrimaryAtStartup; set => Set(ref _projectorPrimaryAtStartup, value); }

    private static readonly Dictionary<string, string> RoleHints = new(StringComparer.OrdinalIgnoreCase)
    {
        [DisplayRoles.Projector] = "GSPro / sim display",
        [DisplayRoles.Tv] = "MySimCaddie lives here",
        [DisplayRoles.Monitor] = "SimTuner swing cameras",
    };

    public static SetupVm From(AppConfig cfg, IReadOnlyList<DisplayInfo> displays)
    {
        var vm = new SetupVm
        {
            RoomName = cfg.RoomName,
            LogoPath = cfg.LogoPath,
            LogoOpacity = cfg.BackgroundLogoOpacity,
            StartWithWindows = cfg.StartWithWindows,
            ProjectorPrimaryAtStartup = cfg.PrimaryDisplayAtStartup.Equals(DisplayRoles.Projector, StringComparison.OrdinalIgnoreCase),
        };

        foreach (var role in DisplayRoles.All)
        {
            var options = new List<DisplayOption> { new() { Display = null, Text = "(not assigned)" } };
            options.AddRange(displays.Select(d => new DisplayOption { Display = d, Text = $"#{d.Number}  {d.Description}" }));

            var current = DisplayService.Resolve(cfg, role, displays);
            vm.Roles.Add(new RoleRowVm
            {
                Role = role,
                Hint = RoleHints.GetValueOrDefault(role, ""),
                Options = options,
                Selected = options.FirstOrDefault(o => o.Display?.GdiName == current?.GdiName && current is not null) ?? options[0],
            });
        }

        foreach (var (id, app) in cfg.Apps)
            vm.Apps.Add(new AppRowVm { Id = id, Name = app.Name, Path = app.Path, RunAsAdmin = app.RunAsAdmin });

        return vm;
    }

    public void ApplyTo(AppConfig cfg)
    {
        cfg.RoomName = string.IsNullOrWhiteSpace(RoomName) ? "My Sim Room" : RoomName.Trim();
        cfg.BackgroundLogoOpacity = Math.Clamp(LogoOpacity, 0, 1);
        cfg.StartWithWindows = StartWithWindows;
        cfg.PrimaryDisplayAtStartup = ProjectorPrimaryAtStartup ? DisplayRoles.Projector : "";
        cfg.LogoPath = StoreLogo(LogoPath);

        foreach (var row in Roles)
        {
            if (row.Selected?.Display is { } d) cfg.Displays[row.Role] = d.ToMatch();
            else cfg.Displays.Remove(row.Role);
        }

        foreach (var row in Apps)
        {
            if (!cfg.Apps.TryGetValue(row.Id, out var app)) continue;
            var oldProcess = app.EffectiveProcessName;
            app.Path = (row.Path ?? "").Trim().Trim('"');
            app.RunAsAdmin = row.RunAsAdmin;

            // If the process name was just the old exe's name, follow the new exe.
            if (string.IsNullOrWhiteSpace(app.ProcessName) || app.ProcessName.Equals(oldProcess, StringComparison.OrdinalIgnoreCase))
            {
                var fromPath = System.IO.Path.GetFileNameWithoutExtension(app.ExpandedPath);
                if (!string.IsNullOrWhiteSpace(fromPath)) app.ProcessName = fromPath;
            }
        }
    }

    /// <summary>Copy the logo into the app's data folder so moving/deleting the original doesn't break the screen.</summary>
    private static string StoreLogo(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        path = path.Trim().Trim('"');
        if (!File.Exists(path)) return path;

        try
        {
            var dataDir = ConfigStore.DataDirectory;
            if (Path.GetFullPath(path).StartsWith(Path.GetFullPath(dataDir), StringComparison.OrdinalIgnoreCase))
                return path;

            Directory.CreateDirectory(dataDir);
            var dest = Path.Combine(dataDir, "logo" + Path.GetExtension(path).ToLowerInvariant());
            File.Copy(path, dest, overwrite: true);
            return dest;
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't copy logo", ex);
            return path;
        }
    }
}
