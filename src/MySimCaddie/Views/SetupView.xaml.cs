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
        ShowTab(scenarios: false);
    }

    // ───────────── Tabs ─────────────

    private void RoomTab_Click(object sender, RoutedEventArgs e) => ShowTab(scenarios: false);

    private void ScenariosTab_Click(object sender, RoutedEventArgs e) => ShowTab(scenarios: true);

    private void ShowTab(bool scenarios)
    {
        RoomPanel.Visibility = scenarios ? Visibility.Collapsed : Visibility.Visible;
        ScenariosPanel.Visibility = scenarios ? Visibility.Visible : Visibility.Collapsed;
        RoomTabButton.Style = (Style)FindResource(scenarios ? "PillButton" : "PrimaryPill");
        ScenariosTabButton.Style = (Style)FindResource(scenarios ? "PrimaryPill" : "PillButton");
        _vm?.SelectedProfile?.RefreshOptions(); // pick up apps added on the Room tab
        if (_vm is not null) _vm.Message = "";
    }

    public void Cancel() => Finished?.Invoke(false);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_cfg is null || _vm is null) return;

        var emptyStep = _vm.Profiles.FirstOrDefault(p => p.Steps.Any(s => string.IsNullOrWhiteSpace(s.AppId)));
        if (emptyStep is not null)
        {
            ShowTab(scenarios: true);
            _vm.SelectedProfile = emptyStep;
            _vm.Message = $"\"{emptyStep.Name}\" has a step with no app picked. Choose one or remove the step.";
            return;
        }

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

    // ───────────── Apps ─────────────

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var row = PickApp();
        if (row is not null && _vm is not null)
            _vm.Message = $"Added {row.Name}. Rename it if you like, then use it in a scenario.";
    }

    /// <summary>Step-level Browse…: find an .exe, add it as an app (or reuse a matching one), and select it.</summary>
    private void BrowseStepApp_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || ((FrameworkElement)sender).DataContext is not StepEditVm step) return;
        var row = PickApp();
        if (row is null) return;
        step.AppId = row.Id;
        _vm.Message = $"Step uses {row.Name}. You can rename apps on the Room & apps tab.";

        if (GsproPreset.IsGspro(row.Path))
        {
            ApplyGsproPreset(step);
            _vm.Message = "GSPro picked: it will press Play! in the launcher pop-up, press Connect in GSPro Connect if needed, and wait for GSPro to open.";
        }
    }

    private static void ApplyGsproPreset(StepEditVm step)
    {
        if (string.IsNullOrWhiteSpace(step.AutoClickWindow)) step.AutoClickWindow = GsproPreset.Window;
        if (string.IsNullOrWhiteSpace(step.AutoClickButton)) step.AutoClickButton = GsproPreset.Button;
        if (string.IsNullOrWhiteSpace(step.AutoClick2Window)) step.AutoClick2Window = GsproPreset.ConnectWindow;
        if (string.IsNullOrWhiteSpace(step.AutoClick2Button)) step.AutoClick2Button = GsproPreset.ConnectButton;
        if (string.IsNullOrWhiteSpace(step.AutoClick2MoveTo)) step.AutoClick2MoveTo = MySimCaddie.Core.Config.DisplayRoles.Monitor;
        if (string.IsNullOrWhiteSpace(step.AutoClick2ThenOpen)) step.AutoClick2ThenOpen = GsproPreset.VisualDataButton;
        if (string.IsNullOrWhiteSpace(step.WaitForProcess)) step.WaitForProcess = "GSPro";
        step.WaitInBackground = true;
        step.Window = MySimCaddie.Core.Config.WindowMode.None; // GSPro goes full-screen on the main display itself
        step.Display = "";
    }

    private void RecordClicks_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || ((FrameworkElement)sender).DataContext is not StepEditVm step) return;

        var proc = !string.IsNullOrWhiteSpace(step.ReplayProcess) ? step.ReplayProcess
            : !string.IsNullOrWhiteSpace(step.WaitForProcess) ? step.WaitForProcess
            : "GSPro";
        var recorder = new ClickRecorderWindow(proc) { Owner = Window.GetWindow(this) };
        recorder.Closed += (_, _) =>
        {
            if (recorder.Result is null) return;
            step.ReplayClicks = recorder.Result;
            step.ReplayProcess = proc;
            _vm.Message = recorder.Result.Count == 0
                ? "No clicks were recorded."
                : $"Recorded {recorder.Result.Count} clicks in {proc}. Save to keep them; they replay after it starts.";
        };
        recorder.Show();
    }

    private void ClearClicks_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || ((FrameworkElement)sender).DataContext is not StepEditVm step) return;
        step.ReplayClicks = new();
        _vm.Message = "Recorded clicks removed. Save to keep the change.";
    }

    private void GsproPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || ((FrameworkElement)sender).DataContext is not StepEditVm step) return;
        step.AutoClickWindow = GsproPreset.Window;
        step.AutoClickButton = GsproPreset.Button;
        step.AutoClick2Window = GsproPreset.ConnectWindow;
        step.AutoClick2Button = GsproPreset.ConnectButton;
        if (string.IsNullOrWhiteSpace(step.AutoClick2MoveTo)) step.AutoClick2MoveTo = MySimCaddie.Core.Config.DisplayRoles.Monitor;
        if (string.IsNullOrWhiteSpace(step.AutoClick2ThenOpen)) step.AutoClick2ThenOpen = GsproPreset.VisualDataButton;
        _vm.Message = "This step will press Play! in the GSPro launcher pop-up, then Connect in GSPro Connect if it doesn't connect by itself.";
    }

    /// <summary>Show a file picker for an .exe. Returns the existing app with that path, a newly added app, or null.</summary>
    private AppRowVm? PickApp()
    {
        if (_vm is null) return null;
        var dlg = new OpenFileDialog
        {
            Title = "Find the program (.exe)",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
        };
        if (Directory.Exists(@"C:\Program Files")) dlg.InitialDirectory = @"C:\Program Files";
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return null;

        var existing = _vm.Apps.FirstOrDefault(a => string.Equals(
            Environment.ExpandEnvironmentVariables(a.Path.Trim().Trim('"')), dlg.FileName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        var name = FriendlyName(dlg.FileName);
        var row = new AppRowVm { Id = _vm.NewAppId(name), Name = name };
        row.Path = dlg.FileName;
        _vm.Apps.Add(row);
        _vm.SelectedProfile?.RefreshOptions();
        return row;
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || ((FrameworkElement)sender).DataContext is not AppRowVm row) return;
        if (_vm.IsAppUsed(row.Id, out var usedBy))
        {
            _vm.Message = $"{row.Name} is used by \"{usedBy}\". Remove it from that scenario first.";
            return;
        }

        _vm.Apps.Remove(row);
        foreach (var p in _vm.Profiles) p.AlsoCloseIds.Remove(row.Id);
        _vm.SelectedProfile?.RefreshOptions();
        _vm.Message = $"Removed {row.Name}.";
    }

    private static string FriendlyName(string exePath)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
            var candidate = !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName : info.FileDescription;
            if (!string.IsNullOrWhiteSpace(candidate) && candidate.Length <= 40) return candidate.Trim();
        }
        catch { /* fall back to the file name */ }

        return Path.GetFileNameWithoutExtension(exePath);
    }

    // ───────────── Scenarios ─────────────

    private void NewScenario_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var p = new ProfileEditVm(_vm.Apps) { Name = "New scenario", Icon = "E768", PrimaryDisplay = DisplayRoles.Projector };
        p.Steps.Add(new StepEditVm { AppOptions = _vm.Apps, Display = DisplayRoles.Projector, Window = WindowMode.Maximize });
        _vm.AddProfile(p);
        _vm.SelectedProfile = p;
        _vm.Message = "Name it, pick the apps, then Save & close.";
    }

    private void DuplicateScenario_Click(object sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedProfile is not { } current) return;
        var copy = current.Clone(current.Name + " (copy)");
        _vm.AddProfile(copy, _vm.Profiles.IndexOf(current) + 1);
        _vm.SelectedProfile = copy;
    }

    private void DeleteScenario_Click(object sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedProfile is not { } current) return;

        // Two taps, like End Session.
        if (!ReferenceEquals(_pendingDelete, current))
        {
            _pendingDelete = current;
            _vm.Message = $"Tap Delete again to remove \"{current.Name}\".";
            return;
        }

        _pendingDelete = null;
        int index = _vm.Profiles.IndexOf(current);
        _vm.Profiles.Remove(current);
        _vm.SelectedProfile = _vm.Profiles.Count == 0 ? null : _vm.Profiles[Math.Min(index, _vm.Profiles.Count - 1)];
        _vm.Message = $"Removed \"{current.Name}\". Back without saving undoes this.";
    }

    private ProfileEditVm? _pendingDelete;

    private void MoveScenarioUp_Click(object sender, RoutedEventArgs e) => MoveScenario(-1);

    private void MoveScenarioDown_Click(object sender, RoutedEventArgs e) => MoveScenario(+1);

    private void MoveScenario(int delta)
    {
        if (_vm?.SelectedProfile is not { } current) return;
        int i = _vm.Profiles.IndexOf(current), j = i + delta;
        if (i < 0 || j < 0 || j >= _vm.Profiles.Count) return;
        _vm.Profiles.Move(i, j);
        _vm.SelectedProfile = current;
    }

    private void AddStep_Click(object sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedProfile is not { } p) return;
        p.Steps.Add(new StepEditVm { AppOptions = _vm.Apps, Display = DisplayRoles.Projector, Window = WindowMode.Maximize });
    }

    private void RemoveStep_Click(object sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedProfile is { } p && ((FrameworkElement)sender).DataContext is StepEditVm step)
            p.Steps.Remove(step);
    }

    private void StepUp_Click(object sender, RoutedEventArgs e) => MoveStep(sender, -1);

    private void StepDown_Click(object sender, RoutedEventArgs e) => MoveStep(sender, +1);

    private void MoveStep(object sender, int delta)
    {
        if (_vm?.SelectedProfile is not { } p || ((FrameworkElement)sender).DataContext is not StepEditVm step) return;
        int i = p.Steps.IndexOf(step), j = i + delta;
        if (i >= 0 && j >= 0 && j < p.Steps.Count) p.Steps.Move(i, j);
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

    /// <summary>The config entry this row came from (null for apps added in this Setup session).</summary>
    public AppDefinition? Original { get; init; }

    private string _name = "";
    public string Name { get => _name; set => Set(ref _name, value ?? ""); }

    private string _processName = "";
    /// <summary>Process name used to detect the app (exe name without .exe).</summary>
    public string ProcessName { get => _processName; set => Set(ref _processName, value ?? ""); }

    private string _path = "";
    public string Path
    {
        get => _path;
        set
        {
            var old = _path;
            if (!Set(ref _path, value ?? "")) return;

            // Follow the exe when the process name was just derived from the old exe.
            var oldDerived = System.IO.Path.GetFileNameWithoutExtension(Environment.ExpandEnvironmentVariables(old ?? ""));
            if (string.IsNullOrWhiteSpace(ProcessName) || ProcessName.Equals(oldDerived, StringComparison.OrdinalIgnoreCase))
            {
                var derived = System.IO.Path.GetFileNameWithoutExtension(Environment.ExpandEnvironmentVariables(_path.Trim().Trim('"')));
                if (!string.IsNullOrWhiteSpace(derived)) ProcessName = derived;
            }

            Raise(nameof(Status));
            Raise(nameof(StatusBrush));
        }
    }

    private bool _runAsAdmin;
    public bool RunAsAdmin { get => _runAsAdmin; set => Set(ref _runAsAdmin, value); }

    private bool Found => !string.IsNullOrWhiteSpace(Path) && File.Exists(Environment.ExpandEnvironmentVariables(Path.Trim().Trim('"')));

    public string Status => string.IsNullOrWhiteSpace(Path) ? "Not set" : Found ? "✓ Found" : "✗ Not found";

    public Brush StatusBrush => (Brush)Application.Current.Resources[Found ? "OkBrush" : "WarnBrush"];

    public AppDefinition ToDefinition()
    {
        var o = Original;
        return new AppDefinition
        {
            Name = string.IsNullOrWhiteSpace(Name) ? Id : Name.Trim(),
            Path = (Path ?? "").Trim().Trim('"'),
            Arguments = o?.Arguments ?? "",
            ProcessName = ProcessName.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase),
            RunAsAdmin = RunAsAdmin,
            Icon = o?.Icon ?? "E768",
            ShowInQuickLaunch = o?.ShowInQuickLaunch ?? true,
            ShowStatus = o?.ShowStatus ?? true,
        };
    }
}

public sealed class SetupVm : Observable
{
    public ObservableCollection<RoleRowVm> Roles { get; } = new();
    public ObservableCollection<AppRowVm> Apps { get; } = new();
    public ObservableCollection<ProfileEditVm> Profiles { get; } = new();

    private ProfileEditVm? _selectedProfile;
    public ProfileEditVm? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (Set(ref _selectedProfile, value))
            {
                value?.RefreshOptions();
                Raise(nameof(HasSelectedProfile));
            }
        }
    }

    public bool HasSelectedProfile => SelectedProfile is not null;

    private string _message = "";
    /// <summary>Short note shown in Setup's footer (validation, hints).</summary>
    public string Message { get => _message; set => Set(ref _message, value ?? ""); }

    private string _roomName = "";
    public string RoomName { get => _roomName; set => Set(ref _roomName, value); }

    private string _logoPath = "";
    public string LogoPath { get => _logoPath; set => Set(ref _logoPath, value); }

    private string _tileLayout = TileLayouts.Left;
    public string TileLayout { get => _tileLayout; set => Set(ref _tileLayout, string.IsNullOrWhiteSpace(value) ? TileLayouts.Left : value); }

    private double _logoOpacity;
    public double LogoOpacity { get => _logoOpacity; set => Set(ref _logoOpacity, Math.Round(value, 2)); }

    private bool _runElevated;
    public bool RunElevated { get => _runElevated; set => Set(ref _runElevated, value); }

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
            TileLayout = cfg.TileLayout,
            StartWithWindows = cfg.StartWithWindows,
            RunElevated = cfg.RunElevated,
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
        {
            var row = new AppRowVm { Id = id, Original = app, Name = app.Name, ProcessName = app.EffectiveProcessName };
            row.Path = app.Path; // after ProcessName so a custom process name (GSPLauncher etc.) is kept
            row.ProcessName = app.EffectiveProcessName;
            row.RunAsAdmin = app.RunAsAdmin;
            vm.Apps.Add(row);
        }

        foreach (var p in cfg.Profiles)
            vm.AddProfile(ProfileEditVm.From(p, vm.Apps, p.Id.Equals(cfg.AutoRunProfile, StringComparison.OrdinalIgnoreCase)));

        vm.SelectedProfile = vm.Profiles.FirstOrDefault();
        return vm;
    }

    public void AddProfile(ProfileEditVm p, int index = -1)
    {
        p.AutoRunChecked = checkedOne =>
        {
            foreach (var other in Profiles.Where(x => x != checkedOne)) other.AutoRun = false;
        };
        if (index < 0 || index > Profiles.Count) Profiles.Add(p);
        else Profiles.Insert(index, p);
    }

    public bool IsAppUsed(string appId, out string usedBy)
    {
        var p = Profiles.FirstOrDefault(x => x.Steps.Any(s => s.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase)));
        usedBy = p?.Name ?? "";
        return p is not null;
    }

    public string NewAppId(string name)
    {
        var slug = new string((name ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (slug.Length == 0) slug = "app";
        var id = slug;
        for (int i = 2; Apps.Any(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase)); i++) id = slug + i;
        return id;
    }

    public void ApplyTo(AppConfig cfg)
    {
        cfg.RoomName = string.IsNullOrWhiteSpace(RoomName) ? "My Sim Room" : RoomName.Trim();
        cfg.BackgroundLogoOpacity = Math.Clamp(LogoOpacity, 0, 1);
        cfg.TileLayout = TileLayout;
        cfg.StartWithWindows = StartWithWindows;
        cfg.RunElevated = RunElevated;
        cfg.PrimaryDisplayAtStartup = ProjectorPrimaryAtStartup ? DisplayRoles.Projector : "";
        cfg.LogoPath = StoreLogo(LogoPath);

        foreach (var row in Roles)
        {
            if (row.Selected?.Display is { } d) cfg.Displays[row.Role] = d.ToMatch();
            else cfg.Displays.Remove(row.Role);
        }

        cfg.Apps = new Dictionary<string, AppDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Apps)
            cfg.Apps[row.Id] = row.ToDefinition();

        // Profiles: give new ones a stable, unique id.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        cfg.Profiles = new List<Profile>();
        cfg.AutoRunProfile = "";
        foreach (var vm in Profiles)
        {
            if (string.IsNullOrWhiteSpace(vm.Id) || used.Contains(vm.Id))
            {
                var spaced = new string((vm.Name ?? "").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
                var slug = string.Join("-", spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                if (slug.Length == 0) slug = "scenario";
                var id = slug;
                for (int i = 2; used.Contains(id); i++) id = $"{slug}-{i}";
                vm.Id = id;
            }

            used.Add(vm.Id);
            cfg.Profiles.Add(vm.ToProfile());
            if (vm.AutoRun) cfg.AutoRunProfile = vm.Id;
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
