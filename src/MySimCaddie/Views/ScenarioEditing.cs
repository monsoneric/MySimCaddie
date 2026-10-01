using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Media;
using MySimCaddie.Core.Config;

namespace MySimCaddie.Views;

// ───────────── Fixed choice lists for the editor ─────────────

public sealed class Choice
{
    public required object Value { get; init; }
    public required string Text { get; init; }
}

public sealed class IconChoice
{
    public required string Hex { get; init; }
    public required string Name { get; init; }
    public string Glyph => Ui.Glyph(Hex);
}

public sealed class ColorChoice
{
    public required string Hex { get; init; }
    public required string Name { get; init; }
    public Brush Brush => Ui.Brush(Hex);
}

public static class SetupChoices
{
    public static List<Choice> Displays { get; } = new()
    {
        new() { Value = "", Text = "Wherever it opens" },
        new() { Value = DisplayRoles.Projector, Text = "Projector" },
        new() { Value = DisplayRoles.Tv, Text = "TV" },
        new() { Value = DisplayRoles.Monitor, Text = "Monitor (27\")" },
    };

    public static List<Choice> PrimaryDisplays { get; } = new()
    {
        new() { Value = "", Text = "Don't change" },
        new() { Value = DisplayRoles.Projector, Text = "Projector" },
        new() { Value = DisplayRoles.Tv, Text = "TV" },
        new() { Value = DisplayRoles.Monitor, Text = "Monitor (27\")" },
    };

    public static List<Choice> Windows { get; } = new()
    {
        new() { Value = WindowMode.Maximize, Text = "Maximize" },
        new() { Value = WindowMode.Fill, Text = "Fill screen (borderless)" },
        new() { Value = WindowMode.Move, Text = "Move, keep size" },
        new() { Value = WindowMode.None, Text = "Don't touch" },
    };

    public static List<Choice> IfRunning { get; } = new()
    {
        new() { Value = IfRunningBehavior.Skip, Text = "Use the running copy" },
        new() { Value = IfRunningBehavior.Restart, Text = "Close and restart it" },
    };

    public static List<IconChoice> Icons { get; } = new()
    {
        new() { Hex = "E7C1", Name = "Flag" },
        new() { Hex = "E768", Name = "Play" },
        new() { Hex = "E714", Name = "Video" },
        new() { Hex = "E722", Name = "Camera" },
        new() { Hex = "E7FC", Name = "Game" },
        new() { Hex = "E734", Name = "Star" },
        new() { Hex = "E945", Name = "Lightning" },
        new() { Hex = "E716", Name = "People" },
        new() { Hex = "E80F", Name = "Home" },
        new() { Hex = "E774", Name = "Globe" },
        new() { Hex = "E7F4", Name = "Screen" },
        new() { Hex = "E8B7", Name = "Folder" },
    };

    public static List<ColorChoice> Colors { get; } = new()
    {
        new() { Hex = "", Name = "Room default" },
        new() { Hex = "#16A34A", Name = "Green" },
        new() { Hex = "#0EA5E9", Name = "Sky" },
        new() { Hex = "#F59E0B", Name = "Amber" },
        new() { Hex = "#8B5CF6", Name = "Violet" },
        new() { Hex = "#E11D48", Name = "Rose" },
        new() { Hex = "#0D9488", Name = "Teal" },
        new() { Hex = "#EA580C", Name = "Orange" },
        new() { Hex = "#475569", Name = "Slate" },
    };
}

// ───────────── Editable copies of profiles and steps ─────────────

public sealed class StepEditVm : Observable
{
    public required ObservableCollection<AppRowVm> AppOptions { get; init; }

    private string _appId = "";
    public string AppId { get => _appId; set => Set(ref _appId, value ?? ""); }

    private string _display = "";
    public string Display { get => _display; set => Set(ref _display, value ?? ""); }

    private WindowMode _window = WindowMode.Maximize;
    public WindowMode Window { get => _window; set => Set(ref _window, value); }

    private bool _closeOnEnd = true;
    public bool CloseOnEnd { get => _closeOnEnd; set => Set(ref _closeOnEnd, value); }

    private string _waitForProcess = "";
    public string WaitForProcess { get => _waitForProcess; set => Set(ref _waitForProcess, value ?? ""); }

    private bool _waitInBackground;
    public bool WaitInBackground { get => _waitInBackground; set => Set(ref _waitInBackground, value); }

    private int _waitTimeoutSeconds = 60;
    public int WaitTimeoutSeconds { get => _waitTimeoutSeconds; set => Set(ref _waitTimeoutSeconds, Math.Clamp(value, 5, 3600)); }

    private int _delayAfterSeconds;
    public int DelayAfterSeconds { get => _delayAfterSeconds; set => Set(ref _delayAfterSeconds, Math.Clamp(value, 0, 600)); }

    private IfRunningBehavior _ifRunning = IfRunningBehavior.Skip;
    public IfRunningBehavior IfRunning { get => _ifRunning; set => Set(ref _ifRunning, value); }

    private int _number;
    public int Number { get => _number; set => Set(ref _number, value); }

    public static StepEditVm From(LaunchStep s, ObservableCollection<AppRowVm> apps) => new()
    {
        AppOptions = apps,
        AppId = s.App,
        Display = s.Display,
        Window = s.Window,
        CloseOnEnd = s.CloseOnEnd,
        WaitForProcess = s.WaitForProcess,
        WaitInBackground = s.WaitInBackground,
        WaitTimeoutSeconds = s.WaitTimeoutSeconds,
        DelayAfterSeconds = s.DelayAfterSeconds,
        IfRunning = s.IfRunning,
    };

    public LaunchStep ToStep() => new()
    {
        App = AppId,
        Display = Display,
        // A window can only be placed when there's a display to place it on.
        Window = string.IsNullOrWhiteSpace(Display) ? WindowMode.None : Window,
        CloseOnEnd = CloseOnEnd,
        WaitForProcess = WaitForProcess.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase),
        WaitInBackground = WaitInBackground,
        WaitTimeoutSeconds = WaitTimeoutSeconds,
        DelayAfterSeconds = DelayAfterSeconds,
        IfRunning = IfRunning,
    };
}

public sealed class CloseOptionVm : Observable
{
    public required string AppId { get; init; }
    public required string Name { get; init; }
    public required Action<CloseOptionVm> Changed { get; init; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (Set(ref _isChecked, value)) Changed(this);
        }
    }
}

public sealed class ProfileEditVm : Observable
{
    private readonly ObservableCollection<AppRowVm> _apps;
    private readonly int _sessionStartTimeoutSeconds;

    public ProfileEditVm(ObservableCollection<AppRowVm> apps, int sessionStartTimeoutSeconds = 600)
    {
        _apps = apps;
        _sessionStartTimeoutSeconds = sessionStartTimeoutSeconds;
        Steps.CollectionChanged += OnStepsChanged;
    }

    public string Id { get; set; } = "";

    private string _name = "";
    public string Name { get => _name; set => Set(ref _name, value ?? ""); }

    private string _subtitle = "";
    public string Subtitle { get => _subtitle; set => Set(ref _subtitle, value ?? ""); }

    private string _icon = "E7C1";
    public string Icon
    {
        get => _icon;
        set
        {
            if (Set(ref _icon, string.IsNullOrWhiteSpace(value) ? "E7C1" : value)) Raise(nameof(Glyph));
        }
    }

    public string Glyph => Ui.Glyph(Icon);

    private string _accentColor = "";
    public string AccentColor
    {
        get => _accentColor;
        set
        {
            if (Set(ref _accentColor, value ?? "")) Raise(nameof(AccentBrush));
        }
    }

    public Brush AccentBrush => string.IsNullOrWhiteSpace(AccentColor)
        ? (Brush)System.Windows.Application.Current.Resources["AccentBrush"]
        : Ui.Brush(AccentColor);

    private string _primaryDisplay = "";
    public string PrimaryDisplay { get => _primaryDisplay; set => Set(ref _primaryDisplay, value ?? ""); }

    private bool _restorePrimaryOnEnd;
    public bool RestorePrimaryOnEnd { get => _restorePrimaryOnEnd; set => Set(ref _restorePrimaryOnEnd, value); }

    private string _sessionProcess = "";
    public string SessionProcess { get => _sessionProcess; set => Set(ref _sessionProcess, value ?? ""); }

    private bool _autoRun;
    public bool AutoRun
    {
        get => _autoRun;
        set
        {
            if (Set(ref _autoRun, value) && value) AutoRunChecked?.Invoke(this);
        }
    }

    public Action<ProfileEditVm>? AutoRunChecked { get; set; }

    public ObservableCollection<StepEditVm> Steps { get; } = new();

    /// <summary>Process names offered in "The round ends when this closes".</summary>
    public ObservableCollection<string> SessionSuggestions { get; } = new();

    public HashSet<string> AlsoCloseIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<CloseOptionVm> AlsoCloseOptions { get; } = new();

    public static ProfileEditVm From(Profile p, ObservableCollection<AppRowVm> apps, bool autoRun)
    {
        var vm = new ProfileEditVm(apps, p.SessionStartTimeoutSeconds)
        {
            Id = p.Id,
            Name = p.Name,
            Subtitle = p.Subtitle,
            Icon = p.Icon,
            AccentColor = p.AccentColor,
            PrimaryDisplay = p.PrimaryDisplay,
            RestorePrimaryOnEnd = p.RestorePrimaryOnEnd,
            SessionProcess = p.SessionProcess,
        };
        vm._autoRun = autoRun;
        foreach (var id in p.AlsoCloseOnEnd) vm.AlsoCloseIds.Add(id);
        foreach (var s in p.Steps) vm.Steps.Add(StepEditVm.From(s, apps));
        return vm;
    }

    public ProfileEditVm Clone(string newName)
    {
        var copy = new ProfileEditVm(_apps, _sessionStartTimeoutSeconds)
        {
            Name = newName,
            Subtitle = Subtitle,
            Icon = Icon,
            AccentColor = AccentColor,
            PrimaryDisplay = PrimaryDisplay,
            RestorePrimaryOnEnd = RestorePrimaryOnEnd,
            SessionProcess = SessionProcess,
        };
        foreach (var id in AlsoCloseIds) copy.AlsoCloseIds.Add(id);
        foreach (var s in Steps) copy.Steps.Add(StepEditVm.From(s.ToStep(), _apps));
        return copy;
    }

    public Profile ToProfile() => new()
    {
        Id = Id,
        Name = string.IsNullOrWhiteSpace(Name) ? "Untitled scenario" : Name.Trim(),
        Subtitle = Subtitle.Trim(),
        Icon = Icon,
        AccentColor = AccentColor,
        PrimaryDisplay = PrimaryDisplay,
        RestorePrimaryOnEnd = RestorePrimaryOnEnd,
        SessionProcess = SessionProcess.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase),
        SessionStartTimeoutSeconds = _sessionStartTimeoutSeconds,
        Steps = Steps.Where(s => !string.IsNullOrWhiteSpace(s.AppId)).Select(s => s.ToStep()).ToList(),
        AlsoCloseOnEnd = AlsoCloseIds.Where(id => _apps.Any(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList(),
    };

    /// <summary>Rebuild the lists that depend on the app list (call when shown, or after apps change).</summary>
    public void RefreshOptions()
    {
        AlsoCloseOptions.Clear();
        foreach (var app in _apps)
        {
            var option = new CloseOptionVm
            {
                AppId = app.Id,
                Name = app.Name,
                Changed = o =>
                {
                    if (o.IsChecked) AlsoCloseIds.Add(o.AppId);
                    else AlsoCloseIds.Remove(o.AppId);
                },
            };
            option.IsChecked = AlsoCloseIds.Contains(app.Id);
            AlsoCloseOptions.Add(option);
        }

        RefreshSuggestions();
    }

    private void OnStepsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (StepEditVm s in e.NewItems) s.PropertyChanged += OnStepPropertyChanged;
        if (e.OldItems is not null)
            foreach (StepEditVm s in e.OldItems) s.PropertyChanged -= OnStepPropertyChanged;

        for (int i = 0; i < Steps.Count; i++) Steps[i].Number = i + 1;
        RefreshSuggestions();
    }

    private void OnStepPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StepEditVm.AppId) or nameof(StepEditVm.WaitForProcess)) RefreshSuggestions();
    }

    private void RefreshSuggestions()
    {
        var names = new List<string>();
        foreach (var s in Steps)
        {
            if (!string.IsNullOrWhiteSpace(s.WaitForProcess)) names.Add(s.WaitForProcess.Trim());
            var app = _apps.FirstOrDefault(a => a.Id.Equals(s.AppId, StringComparison.OrdinalIgnoreCase));
            if (app is not null && !string.IsNullOrWhiteSpace(app.ProcessName)) names.Add(app.ProcessName);
        }

        var distinct = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.SequenceEqual(SessionSuggestions, StringComparer.OrdinalIgnoreCase)) return;

        // Clearing an editable ComboBox's items can wipe its text; keep the value safe.
        var keep = SessionProcess;
        SessionSuggestions.Clear();
        foreach (var n in distinct) SessionSuggestions.Add(n);
        SessionProcess = keep;
    }
}
