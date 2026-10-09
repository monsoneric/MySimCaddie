using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MySimCaddie.Core;
using MySimCaddie.Core.Config;
using MySimCaddie.Core.Displays;
using MySimCaddie.Core.Launch;
using MySimCaddie.Core.Platform;
using MySimCaddie.Services;
using MySimCaddie.Views;

namespace MySimCaddie;

public partial class MainWindow : Window
{
    private AppConfig _cfg;
    private readonly MainVm _vm = new();
    private readonly GlobalHotkey _hotkey = new(0xB001);
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly bool _showSetupOnLoad;

    private CancellationTokenSource? _sessionCts;
    private DateTime _endArmedUntil = DateTime.MinValue;

    public MainWindow(AppConfig config, bool showSetup)
    {
        InitializeComponent();
        _cfg = config;
        _showSetupOnLoad = showSetup;
        DataContext = _vm;

        ApplyConfig();

        _clockTimer.Tick += (_, _) => UpdateClock();
        _statusTimer.Tick += (_, _) => RefreshAppChips();
        _toastTimer.Tick += (_, _) => { _vm.IsToastVisible = false; _toastTimer.Stop(); };
        UpdateClock();
        _clockTimer.Start();
        _statusTimer.Start();

        SetupPanel.Finished += OnSetupFinished;
        SetupPanel.IdentifyRequested += () => IdentifyDisplays();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
        Closing += Window_Closing;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        if (ConfigStore.LastLoadError is { } err)
            ShowToast($"config.json had an error, so defaults are in use ({err})");
    }

    // ───────────── Lifecycle ─────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        PlaceOnLauncherDisplay();

        _hotkey.Pressed += BringToFront;
        if (!_hotkey.Register(this, GlobalHotkey.MOD_CONTROL | GlobalHotkey.MOD_ALT, 0x48 /* H */))
            Log.Warn("Ctrl+Alt+H is in use by another app");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        FocusFirstTile();

        if (_showSetupOnLoad)
        {
            OpenSetup();
            ShowToast("Welcome! Check your displays and app locations, then press Save.");
        }
        else if (App.LaunchedAtLogin && !string.IsNullOrWhiteSpace(_cfg.AutoRunProfile))
        {
            var p = _cfg.Profiles.FirstOrDefault(x => x.Id.Equals(_cfg.AutoRunProfile, StringComparison.OrdinalIgnoreCase));
            if (p is not null) _ = StartSessionAsync(p);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _hotkey.Dispose();
        _sessionCts?.Cancel();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Fires off the UI thread, and also when we change the primary display ourselves.
        Dispatcher.InvokeAsync(() =>
        {
            PlaceOnLauncherDisplay();
            RefreshDisplayChips();
        });
    }

    private void PlaceOnLauncherDisplay()
    {
        var displays = DisplayService.GetDisplays();
        if (displays.Count == 0) return;

        var target = DisplayService.Resolve(_cfg, _cfg.LauncherDisplay, displays)
                     ?? displays.FirstOrDefault(d => !d.IsPrimary)
                     ?? displays[0];
        WpfWindowTools.CoverDisplay(this, target);
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;  // the classic nudge past focus-stealing prevention
        Topmost = false;
        if (!_vm.IsSessionActive && !_vm.IsSetupOpen) FocusFirstTile();
    }

    // ───────────── Config → screen ─────────────

    private void ApplyConfig()
    {
        var accent = Ui.Brush(_cfg.AccentColor);
        Application.Current.Resources["AccentBrush"] = accent;
        Application.Current.Resources["AccentSoftBrush"] = Ui.Soft(accent);

        _vm.RoomName = _cfg.RoomName;
        _vm.Logo = Ui.LoadImage(_cfg.LogoPath);
        _vm.LogoOpacity = Math.Clamp(_cfg.BackgroundLogoOpacity, 0, 1);
        ApplyTileLayout(_cfg.TileLayout);

        _vm.Tiles.Clear();
        foreach (var p in _cfg.Profiles)
            _vm.Tiles.Add(new TileVm { Profile = p, Accent = Ui.Brush(p.AccentColor, _cfg.AccentColor) });

        _vm.QuickItems.Clear();
        foreach (var (id, app) in _cfg.Apps.Where(a => a.Value.ShowInQuickLaunch))
        {
            var captured = app;
            _vm.QuickItems.Add(new QuickItemVm
            {
                Name = app.Name,
                Glyph = Ui.Glyph(app.Icon),
                ToolTip = app.ExpandedPath,
                Invoke = () =>
                {
                    var error = AppLauncher.Start(captured);
                    ShowToast(error ?? $"Starting {captured.Name}…");
                },
            });
        }

        foreach (var link in _cfg.QuickLinks)
        {
            var captured = link;
            _vm.QuickItems.Add(new QuickItemVm
            {
                Name = link.Name,
                Glyph = Ui.Glyph(link.Icon),
                ToolTip = link.Target,
                Invoke = () =>
                {
                    var error = AppLauncher.Open(captured.Target);
                    if (error is not null) ShowToast($"Couldn't open {captured.Name}: {error}");
                },
            });
        }

        _vm.AppChips.Clear();
        foreach (var app in _cfg.Apps.Values.Where(a => a.ShowStatus))
            _vm.AppChips.Add(new StatusChipVm { Label = app.Name, ProcessName = app.EffectiveProcessName });

        RefreshDisplayChips();
        RefreshAppChips();
        RefreshTileReadiness();
    }

    /// <summary>Tiles in a side column (Left/Right) or a row across the top; the logo gets the rest of the space.</summary>
    private void ApplyTileLayout(string? layout)
    {
        var tileColumn = new GridLength(440);
        var star = new GridLength(1, GridUnitType.Star);

        void Place(UIElement e, int row, int col, int rowSpan, int colSpan)
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            Grid.SetRowSpan(e, rowSpan);
            Grid.SetColumnSpan(e, colSpan);
        }

        switch ((layout ?? "").Trim().ToLowerInvariant())
        {
            case "right":
                ColA.Width = star; ColGap.Width = new GridLength(56); ColB.Width = tileColumn;
                RowTop.Height = star; RowBottom.Height = new GridLength(0);
                Place(TilesArea, 0, 2, 2, 1);
                Place(LogoArea, 0, 0, 2, 1);
                LogoArea.Margin = new Thickness(0);
                break;

            case "top":
                ColA.Width = star; ColGap.Width = new GridLength(0); ColB.Width = new GridLength(0);
                RowTop.Height = GridLength.Auto; RowBottom.Height = star;
                Place(TilesArea, 0, 0, 1, 3);
                Place(LogoArea, 1, 0, 1, 3);
                LogoArea.Margin = new Thickness(0, 16, 0, 0);
                break;

            default: // left
                ColA.Width = tileColumn; ColGap.Width = new GridLength(56); ColB.Width = star;
                RowTop.Height = star; RowBottom.Height = new GridLength(0);
                Place(TilesArea, 0, 0, 2, 1);
                Place(LogoArea, 0, 2, 2, 1);
                LogoArea.Margin = new Thickness(0);
                break;
        }
    }

    private void RefreshDisplayChips()
    {
        var displays = DisplayService.GetDisplays();
        _vm.DisplayChips.Clear();
        foreach (var role in DisplayRoles.All)
        {
            var d = DisplayService.Resolve(_cfg, role, displays);
            _vm.DisplayChips.Add(new StatusChipVm
            {
                Label = role,
                Detail = d is null ? "not found" : d.DisplayName + (d.IsPrimary ? " · main" : ""),
                Dot = (Brush)FindResource(d is null ? "BadBrush" : "OkBrush"),
            });
        }
    }

    private void RefreshAppChips()
    {
        foreach (var chip in _vm.AppChips)
        {
            bool running = ProcessTools.IsRunning(chip.ProcessName);
            chip.Detail = running ? "running" : "idle";
            chip.Dot = (Brush)FindResource(running ? "OkBrush" : "IdleBrush");
        }
    }

    private void RefreshTileReadiness()
    {
        foreach (var tile in _vm.Tiles)
        {
            var problems = tile.Profile.Steps
                .Select(s => _cfg.Apps.TryGetValue(s.App, out var a) ? AppLauncher.Problem(a) : $"unknown app {s.App}")
                .Where(p => p is not null)
                .ToList();
            tile.IsReady = problems.Count == 0;
            tile.ReadyText = tile.IsReady ? "Ready" : "Setup needed · " + problems[0];
        }
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        _vm.Time = now.ToString("h:mm");
        _vm.Date = now.ToString("dddd, MMMM d");
    }

    // ───────────── Sessions ─────────────

    private async void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not TileVm tile) return;
        if (_vm.IsSessionActive)
        {
            ShowToast($"{_vm.SessionTitle} is still running. Use Back to round to end it first.");
            return;
        }

        await StartSessionAsync(tile.Profile);
    }

    private Profile? _sessionProfile;

    private async Task StartSessionAsync(Profile profile)
    {
        if (_sessionCts is not null) return;
        _sessionProfile = profile;
        _vm.GameName = profile.SessionProcess?.Trim() ?? "";

        _vm.Steps.Clear();
        foreach (var title in ProfileRunner.DescribeSteps(_cfg, profile))
            _vm.Steps.Add(new StepVm { Title = title });

        _vm.SessionTitle = profile.Name;
        _vm.SessionPhase = "GETTING READY";
        _vm.SessionMessage = "Starting up…";
        _vm.SessionHint = "";
        _vm.CanEndSession = true;
        _vm.IsSessionActive = true;
        EndSessionText.Text = "End Session";

        _sessionCts = new CancellationTokenSource();
        var runner = new ProfileRunner(_cfg, profile, new Progress<RunnerUpdate>(OnRunnerUpdate));
        try
        {
            await Task.Run(() => runner.RunAsync(_sessionCts.Token));
        }
        catch (Exception ex)
        {
            Log.Error("Session crashed", ex);
            ShowToast($"Session stopped: {ex.Message}");
        }
        finally
        {
            _sessionCts.Dispose();
            _sessionCts = null;
        }

        // Leave the final checklist up briefly when something needs attention.
        bool anyProblem = _vm.Steps.Any(s => s.State is StepState.Failed or StepState.Warning);
        await Task.Delay(anyProblem ? 6000 : 1200);

        _vm.IsSessionActive = false;
        RefreshAppChips();
        RefreshDisplayChips();
        BringToFront();
    }

    private void OnRunnerUpdate(RunnerUpdate u)
    {
        _vm.SessionMessage = u.Message;
        _vm.SessionPhase = u.Phase switch
        {
            SessionPhase.Starting => "GETTING READY",
            SessionPhase.WaitingForGame => "ALMOST THERE",
            SessionPhase.Playing => "ON THE TEE",
            SessionPhase.Ending => "WRAPPING UP",
            _ => "DONE",
        };
        _vm.SessionHint = u.Phase switch
        {
            SessionPhase.WaitingForGame => "MySimCaddie presses Play in the GSPro launcher for you. If it's still showing after a few seconds, press Play yourself.",
            SessionPhase.Playing => "Close GSPro when you're done and everything tidies up automatically.",
            _ => "",
        };
        _vm.CanEndSession = u.Phase is not (SessionPhase.Ending or SessionPhase.Finished);

        if (u.StepIndex is int i && i >= 0 && i < _vm.Steps.Count && u.StepState is StepState state)
        {
            _vm.Steps[i].State = state;
            _vm.Steps[i].Detail = u.Message;
        }
    }

    private void HideSessionCard_Click(object sender, RoutedEventArgs e)
    {
        _vm.IsSessionCardHidden = true;
        Log.Info("Session card hidden; round keeps running");
    }

    private void ShowSessionCard_Click(object sender, RoutedEventArgs e)
    {
        _vm.IsSessionCardHidden = false;
        Log.Info("Session card shown again");
    }

    /// <summary>Switch back into the game (e.g. GSPro) without ending anything.</summary>
    private void BackToGame_Click(object sender, RoutedEventArgs e)
    {
        var proc = _sessionProfile?.SessionProcess;
        if (string.IsNullOrWhiteSpace(proc))
        {
            ShowToast("This scenario has no game to go back to");
            return;
        }

        _vm.IsSessionCardHidden = false;
        bool ok = WindowPlacer.BringToFront(proc);
        Log.Info(ok ? $"Switched back to {proc}" : $"Couldn't bring {proc} to the front");
        if (!ok) ShowToast($"Couldn't find {proc}'s window");
    }

    private void EndSession_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionCts is null) return;

        // Two taps so a stray remote press doesn't end a round.
        if (DateTime.UtcNow > _endArmedUntil)
        {
            _endArmedUntil = DateTime.UtcNow.AddSeconds(4);
            EndSessionText.Text = "Tap again to end";
            Later.Run(TimeSpan.FromSeconds(4), () => { if (_sessionCts is not null) EndSessionText.Text = "End Session"; });
            return;
        }

        EndSessionText.Text = "Ending…";
        _sessionCts.Cancel();
    }

    // ───────────── Footer actions ─────────────

    private void Quick_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is QuickItemVm item) item.Invoke();
    }

    private void Identify_Click(object sender, RoutedEventArgs e) => IdentifyDisplays();

    private void IdentifyDisplays()
    {
        var displays = DisplayService.GetDisplays();
        foreach (var d in displays)
        {
            var role = DisplayRoles.All.FirstOrDefault(r =>
                DisplayService.Resolve(_cfg, r, displays) is { } match && match.GdiName == d.GdiName) ?? "";
            new IdentifyWindow(d, role).Show();
        }
    }

    private void Setup_Click(object sender, RoutedEventArgs e) => OpenSetup();

    private void OpenSetup()
    {
        SetupPanel.Open(_cfg);
        _vm.IsSetupOpen = true;
    }

    private void OnSetupFinished(bool saved)
    {
        _vm.IsSetupOpen = false;
        if (saved)
        {
            try
            {
                ConfigStore.Save(_cfg);
                if (Environment.ProcessPath is { } exe)
                {
                    if (StartupRegistration.Sync(_cfg, exe)) ConfigStore.Save(_cfg);

                    if (_cfg.RunElevated && !StartupRegistration.IsElevated)
                    {
                        ShowToast("Restarting MySimCaddie as administrator…");
                        var app = (App)Application.Current;
                        Later.Run(TimeSpan.FromSeconds(1.5), () =>
                        {
                            _exiting = true; // skip the exit prompt path
                            if (!app.RelaunchElevated(exe))
                            {
                                _exiting = false;
                                ShowToast("Couldn't restart as administrator (was the Windows prompt declined?)");
                            }
                        });
                    }
                }
                ShowToast("Setup saved");
            }
            catch (Exception ex)
            {
                Log.Error("Saving config failed", ex);
                ShowToast($"Couldn't save setup: {ex.Message}");
            }

            ApplyConfig();
            PlaceOnLauncherDisplay();
        }

        FocusFirstTile();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsSessionActive)
        {
            ShowToast("End the session first");
            return;
        }

        _vm.IsExitPromptOpen = true;
        Dispatcher.InvokeAsync(() => ExitConfirmButton.Focus(), DispatcherPriority.Loaded);
    }

    private void ExitConfirm_Click(object sender, RoutedEventArgs e) => ExitApp();

    private void ExitDesktop_Click(object sender, RoutedEventArgs e)
    {
        _vm.IsExitPromptOpen = false;
        WindowState = WindowState.Minimized;
    }

    private void ExitCancel_Click(object sender, RoutedEventArgs e)
    {
        _vm.IsExitPromptOpen = false;
        FocusFirstTile();
    }

    private bool _exiting;

    /// <summary>Close for real. Falls back to a hard exit if anything keeps the process alive.</summary>
    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info("Exit requested");
        _sessionCts?.Cancel();

        var watchdog = new Thread(() =>
        {
            Thread.Sleep(3000);
            Log.Warn("Normal shutdown stalled; forcing exit");
            Environment.Exit(0);
        }) { IsBackground = true };
        watchdog.Start();

        Application.Current.Shutdown();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Alt+F4 lands here. Let it exit, but go through the same path so the watchdog applies.
        if (!_exiting)
        {
            e.Cancel = true;
            Dispatcher.InvokeAsync(ExitApp);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm.IsExitPromptOpen)
        {
            _vm.IsExitPromptOpen = false;
            FocusFirstTile();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _vm.IsSetupOpen)
        {
            SetupPanel.Cancel();
            e.Handled = true;
        }
        else if (e.Key == Key.Q && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Exit_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            RefreshDisplayChips();
            RefreshAppChips();
            RefreshTileReadiness();
            e.Handled = true;
        }
    }

    // ───────────── Helpers ─────────────

    public void ShowToast(string message)
    {
        _vm.Toast = message;
        _vm.IsToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void FocusFirstTile()
    {
        Dispatcher.InvokeAsync(() =>
        {
            var first = FindDescendant<Button>(TilesList);
            first?.Focus();
        }, DispatcherPriority.Loaded);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var deeper = FindDescendant<T>(child);
            if (deeper is not null) return deeper;
        }

        return null;
    }
}

internal static class Later
{
    /// <summary>Run an action once on the UI thread after a delay.</summary>
    public static void Run(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }
}
