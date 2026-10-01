using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MySimCaddie.Core.Config;
using MySimCaddie.Core.Launch;

namespace MySimCaddie;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class Ui
{
    /// <summary>"E7C1" → the Segoe Fluent Icons glyph.</summary>
    public static string Glyph(string? hex)
    {
        try { return char.ConvertFromUtf32(Convert.ToInt32(string.IsNullOrWhiteSpace(hex) ? "E768" : hex.Trim(), 16)); }
        catch { return char.ConvertFromUtf32(0xE768); }
    }

    public static SolidColorBrush Brush(string? color, string fallback = "#16A34A")
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(string.IsNullOrWhiteSpace(color) ? fallback : color)); }
        catch { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback)); }
    }

    /// <summary>Same hue, mixed 85% toward white — for soft icon backgrounds.</summary>
    public static SolidColorBrush Soft(SolidColorBrush b)
    {
        var c = b.Color;
        byte Mix(byte v) => (byte)(v + (255 - v) * 0.85);
        return new SolidColorBrush(Color.FromRgb(Mix(c.R), Mix(c.G), Mix(c.B)));
    }

    public static BitmapImage? LoadImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad; // don't lock the file
            img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            img.UriSource = new Uri(path, UriKind.Absolute);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
    }
}

public sealed class TileVm : Observable
{
    public required Profile Profile { get; init; }
    public string Name => Profile.Name;
    public string Subtitle => Profile.Subtitle;
    public string Glyph => Ui.Glyph(Profile.Icon);
    public required SolidColorBrush Accent { get; init; }
    public SolidColorBrush AccentSoft => Ui.Soft(Accent);

    private string _readyText = "Ready";
    public string ReadyText { get => _readyText; set => Set(ref _readyText, value); }

    private bool _isReady = true;
    public bool IsReady { get => _isReady; set => Set(ref _isReady, value); }
}

public sealed class QuickItemVm
{
    public required string Name { get; init; }
    public required string Glyph { get; init; }
    public required Action Invoke { get; init; }
    public string ToolTip { get; init; } = "";
}

public sealed class StatusChipVm : Observable
{
    public required string Label { get; init; }

    private string _detail = "";
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    private Brush _dot = Brushes.LightGray;
    public Brush Dot { get => _dot; set => Set(ref _dot, value); }

    /// <summary>For app chips: process to check.</summary>
    public string ProcessName { get; init; } = "";
}

public sealed class StepVm : Observable
{
    public required string Title { get; init; }

    private StepState _state = StepState.Pending;
    public StepState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
            {
                Raise(nameof(Glyph));
                Raise(nameof(Color));
            }
        }
    }

    private string _detail = "";
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    public string Glyph => Ui.Glyph(State switch
    {
        StepState.Running => "E768",  // play
        StepState.Done => "E73E",     // check
        StepState.Skipped => "E73E",
        StepState.Warning => "E7BA",  // warning
        StepState.Failed => "E711",   // cancel
        _ => "EA3A",                  // circle
    });

    public Brush Color => State switch
    {
        StepState.Running => (Brush)System.Windows.Application.Current.Resources["AccentBrush"],
        StepState.Done or StepState.Skipped => (Brush)System.Windows.Application.Current.Resources["OkBrush"],
        StepState.Warning => (Brush)System.Windows.Application.Current.Resources["WarnBrush"],
        StepState.Failed => (Brush)System.Windows.Application.Current.Resources["BadBrush"],
        _ => (Brush)System.Windows.Application.Current.Resources["IdleBrush"],
    };
}

public sealed class MainVm : Observable
{
    private string _roomName = "";
    public string RoomName { get => _roomName; set => Set(ref _roomName, value); }

    private ImageSource? _logo;
    public ImageSource? Logo { get => _logo; set { Set(ref _logo, value); Raise(nameof(HasLogo)); } }
    public bool HasLogo => Logo is not null;

    private double _logoOpacity = 0.22;
    public double LogoOpacity { get => _logoOpacity; set => Set(ref _logoOpacity, value); }

    private string _time = "";
    public string Time { get => _time; set => Set(ref _time, value); }

    private string _date = "";
    public string Date { get => _date; set => Set(ref _date, value); }

    public ObservableCollection<TileVm> Tiles { get; } = new();
    public ObservableCollection<QuickItemVm> QuickItems { get; } = new();
    public ObservableCollection<StatusChipVm> DisplayChips { get; } = new();
    public ObservableCollection<StatusChipVm> AppChips { get; } = new();

    // ── Session ──
    private bool _isSessionActive;
    public bool IsSessionActive { get => _isSessionActive; set => Set(ref _isSessionActive, value); }

    private string _sessionTitle = "";
    public string SessionTitle { get => _sessionTitle; set => Set(ref _sessionTitle, value); }

    private string _sessionMessage = "";
    public string SessionMessage { get => _sessionMessage; set => Set(ref _sessionMessage, value); }

    private string _sessionPhase = "";
    public string SessionPhase { get => _sessionPhase; set => Set(ref _sessionPhase, value); }

    private string _sessionHint = "";
    public string SessionHint { get => _sessionHint; set => Set(ref _sessionHint, value); }

    private bool _canEndSession;
    public bool CanEndSession { get => _canEndSession; set => Set(ref _canEndSession, value); }

    public ObservableCollection<StepVm> Steps { get; } = new();

    // ── Toast ──
    private string _toast = "";
    public string Toast { get => _toast; set => Set(ref _toast, value); }

    private bool _isToastVisible;
    public bool IsToastVisible { get => _isToastVisible; set => Set(ref _isToastVisible, value); }

    private bool _isSetupOpen;
    public bool IsSetupOpen { get => _isSetupOpen; set => Set(ref _isSetupOpen, value); }
}
