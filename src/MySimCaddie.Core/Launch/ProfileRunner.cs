using MySimCaddie.Core.Config;
using MySimCaddie.Core.Displays;
using MySimCaddie.Core.Platform;

namespace MySimCaddie.Core.Launch;

public enum SessionPhase
{
    Starting,
    WaitingForGame,
    Playing,
    Ending,
    Finished,
}

public enum StepState
{
    Pending,
    Running,
    Done,
    Skipped,
    Warning,
    Failed,
}

/// <summary>A progress report from the runner. StepIndex is set when the update concerns a specific step.</summary>
public sealed record RunnerUpdate(SessionPhase Phase, string Message, int? StepIndex = null, StepState? StepState = null);

/// <summary>
/// Runs a profile: set the primary display, launch each step in order, place windows, then hold the session
/// open until the session process (e.g. GSPro) exits or End Session is pressed, and clean up companions.
/// </summary>
public sealed class ProfileRunner
{
    private readonly AppConfig _cfg;
    private readonly Profile _profile;
    private readonly IProgress<RunnerUpdate> _progress;

    public ProfileRunner(AppConfig cfg, Profile profile, IProgress<RunnerUpdate> progress)
    {
        _cfg = cfg;
        _profile = profile;
        _progress = progress;
    }

    /// <summary>Titles for each step, for the UI's checklist.</summary>
    public static IReadOnlyList<string> DescribeSteps(AppConfig cfg, Profile profile)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile.PrimaryDisplay))
            list.Add($"{profile.PrimaryDisplay} as main display");
        foreach (var s in profile.Steps)
        {
            var name = cfg.Apps.TryGetValue(s.App, out var a) ? a.Name : s.App;
            list.Add(string.IsNullOrWhiteSpace(s.Display) || s.Window == WindowMode.None ? $"Start {name}" : $"Start {name} → {s.Display}");
        }

        if (!string.IsNullOrWhiteSpace(profile.SessionProcess))
            list.Add($"Play ({profile.SessionProcess})");
        return list;
    }

    /// <param name="endRequested">Cancel this to end the session (closes the session process and companions).</param>
    public async Task RunAsync(CancellationToken endRequested)
    {
        Log.Info($"── Session start: {_profile.Name}");
        _background = CancellationTokenSource.CreateLinkedTokenSource(endRequested);
        DisplayInfo? previousPrimary = null;
        bool hasPrimaryStep = !string.IsNullOrWhiteSpace(_profile.PrimaryDisplay);
        int stepOffset = hasPrimaryStep ? 1 : 0;
        int playIndex = stepOffset + _profile.Steps.Count;
        bool hasSession = !string.IsNullOrWhiteSpace(_profile.SessionProcess);
        bool cancelled = false;

        try
        {
            Report(SessionPhase.Starting, $"Starting {_profile.Name}…");

            if (hasPrimaryStep)
                previousPrimary = EnsurePrimary(0);

            for (int i = 0; i < _profile.Steps.Count; i++)
            {
                endRequested.ThrowIfCancellationRequested();
                await RunStepAsync(stepOffset + i, _profile.Steps[i], endRequested);
            }

            if (!hasSession)
            {
                Report(SessionPhase.Finished, $"{_profile.Name} is up");
                return;
            }

            var proc = _profile.SessionProcess;
            if (!ProcessTools.IsRunning(proc))
            {
                Report(SessionPhase.WaitingForGame, $"Waiting for {proc}… (press Play in its launcher if it asks)", playIndex, StepState.Running);
                var started = await ProcessTools.WaitForStartAsync(proc, TimeSpan.FromSeconds(Math.Max(10, _profile.SessionStartTimeoutSeconds)), endRequested);
                if (!started)
                {
                    Report(SessionPhase.Ending, $"{proc} didn't start within {_profile.SessionStartTimeoutSeconds / 60.0:0.#} min", playIndex, StepState.Failed);
                    return; // finally{} tidies up
                }
            }

            Report(SessionPhase.Playing, $"{proc} is running — enjoy the round", playIndex, StepState.Running);
            while (ProcessTools.IsRunning(proc))
                await Task.Delay(2000, endRequested);

            Report(SessionPhase.Ending, $"{proc} closed", playIndex, StepState.Done);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            Log.Info("End Session requested");
            if (hasSession)
            {
                Report(SessionPhase.Ending, $"Closing {_profile.SessionProcess}…", playIndex, StepState.Done);
                await ProcessTools.CloseAsync(_profile.SessionProcess, TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Session failed", ex);
            Report(SessionPhase.Ending, $"Something went wrong: {ex.Message}");
        }
        finally
        {
            // Launch-only profiles return straight away; let their auto-click keep watching.
            if (hasSession || cancelled) _background.Cancel();

            // Profiles without a session process just launch things; leave them running.
            if (hasSession || cancelled)
                await CleanupAsync(previousPrimary);

            Report(SessionPhase.Finished, "Back at the clubhouse");
            Log.Info($"── Session end: {_profile.Name}");
        }
    }

    private DisplayInfo? EnsurePrimary(int index)
    {
        var target = DisplayService.Resolve(_cfg, _profile.PrimaryDisplay);
        if (target is null)
        {
            Report(SessionPhase.Starting, $"{_profile.PrimaryDisplay} display isn't assigned or connected — open Setup", index, StepState.Warning);
            return null;
        }

        if (target.IsPrimary)
        {
            Report(SessionPhase.Starting, $"{_profile.PrimaryDisplay} is already the main display", index, StepState.Done);
            return null;
        }

        var previous = DisplayService.GetPrimary();
        var ok = DisplayService.SetPrimary(target);
        Report(SessionPhase.Starting,
            ok ? $"{_profile.PrimaryDisplay} set as main display" : $"Couldn't make {_profile.PrimaryDisplay} the main display",
            index, ok ? StepState.Done : StepState.Warning);
        return ok ? previous : null;
    }

    private async Task RunStepAsync(int index, LaunchStep step, CancellationToken ct)
    {
        if (!_cfg.Apps.TryGetValue(step.App, out var app))
        {
            Report(SessionPhase.Starting, $"Unknown app \"{step.App}\" in profile", index, StepState.Failed);
            return;
        }

        var proc = app.EffectiveProcessName;
        var waitName = string.IsNullOrWhiteSpace(step.WaitForProcess) ? proc : step.WaitForProcess.Trim();
        Report(SessionPhase.Starting, $"Starting {app.Name}…", index, StepState.Running);

        bool alreadyRunning = ProcessTools.IsRunning(waitName) || ProcessTools.IsRunning(proc);
        if (alreadyRunning && step.IfRunning == IfRunningBehavior.Restart)
        {
            await ProcessTools.CloseAsync(waitName, TimeSpan.FromSeconds(8));
            if (!waitName.Equals(proc, StringComparison.OrdinalIgnoreCase))
                await ProcessTools.CloseAsync(proc, TimeSpan.FromSeconds(8));
            alreadyRunning = false;
        }

        if (!alreadyRunning)
        {
            var error = AppLauncher.Start(app);
            if (error is not null)
            {
                Report(SessionPhase.Starting, error, index, StepState.Failed);
                return;
            }

            if (!step.WaitInBackground)
            {
                var timeout = TimeSpan.FromSeconds(Math.Max(5, step.WaitTimeoutSeconds));
                if (!await ProcessTools.WaitForStartAsync(waitName, timeout, ct))
                {
                    Report(SessionPhase.Starting, $"{app.Name} didn't start within {timeout.TotalSeconds:0}s", index, StepState.Failed);
                    return;
                }
            }
        }

        var state = alreadyRunning ? StepState.Skipped : StepState.Done;
        var message = alreadyRunning ? $"{app.Name} was already running" : $"{app.Name} started";

        if (!string.IsNullOrWhiteSpace(step.Display) && step.Window != WindowMode.None)
        {
            var display = DisplayService.Resolve(_cfg, step.Display);
            if (display is null)
            {
                state = StepState.Warning;
                message += $" — {step.Display} display isn't assigned or connected";
            }
            else
            {
                Report(SessionPhase.Starting, $"Moving {app.Name} to the {step.Display}…", index, StepState.Running);
                var placed = await WindowPlacer.PlaceAsync(waitName, display, step.Window,
                    TimeSpan.FromSeconds(Math.Max(5, step.WaitTimeoutSeconds)), ct);
                if (placed)
                {
                    message += $" on the {step.Display}";
                }
                else
                {
                    state = StepState.Warning;
                    message += $" — couldn't move its window to the {step.Display}";
                }
            }
        }

        Report(SessionPhase.Starting, message, index, state);

        if (!string.IsNullOrWhiteSpace(step.AutoClickWindow) && !string.IsNullOrWhiteSpace(step.AutoClickButton))
            _ = AutoClickAsync(index, step, step.AutoClickWindow, step.AutoClickButton, optional: false, quiet: alreadyRunning, _background.Token);

        if (!string.IsNullOrWhiteSpace(step.AutoClick2Window) && !string.IsNullOrWhiteSpace(step.AutoClick2Button))
            _ = AutoClickAsync(index, step, step.AutoClick2Window, step.AutoClick2Button, optional: true, quiet: alreadyRunning, _background.Token);

        if (step.DelayAfterSeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(step.DelayAfterSeconds), ct);
    }

    /// <summary>Runs alongside the rest of the session: waits for the pop-up and presses its button.</summary>
    private async Task AutoClickAsync(int index, LaunchStep step, string windowTitle, string buttonText, bool optional, bool quiet,
        CancellationToken ct)
    {
        var window = windowTitle.Trim();
        var button = buttonText.Trim();
        // An optional press (e.g. Connect) shows up later than the first pop-up, so it always gets the full time.
        var timeout = TimeSpan.FromSeconds(quiet && !optional ? 10 : Math.Clamp(step.AutoClickTimeoutSeconds, 10, 1800));
        var options = optional
            ? new AutoClickOptions { Optional = true, Settle = TimeSpan.FromSeconds(6), MaxAttempts = 3, WaitForSelection = true }
            : AutoClickOptions.Default;
        try
        {
            // Pop-ups like GSPro's come from the launcher or the game it opens.
            var hints = new List<string> { step.WaitForProcess, "GSPro", "GSPLauncher", "GSPconnect" };
            if (_cfg.Apps.TryGetValue(step.App, out var app)) hints.Add(app.EffectiveProcessName);
            if (!string.IsNullOrWhiteSpace(_profile.SessionProcess)) hints.Add(_profile.SessionProcess);

            var result = await AutoClicker.ClickAsync(window, button, hints, timeout, ct, options);
            if (result == AutoClickResult.Clicked)
                Report(_phase, $"Pressed \"{button}\" in {window}", index, StepState.Done);
            else if (result == AutoClickResult.NeverReady)
                Report(_phase, $"{window} never found the launch monitor, so \"{button}\" wasn't pressed — check it's on, then press Search and Connect yourself", index, StepState.Warning);
            else if (result == AutoClickResult.StillShowing)
                Report(_phase, $"Pressed \"{button}\" in {window} but it didn't take — check the launch monitor is on, then press it yourself", index, StepState.Warning);
            else if (result == AutoClickResult.Blocked && !optional)
                Report(_phase, $"Windows blocked pressing \"{button}\" because the program runs as administrator. " +
                               "Turn on Setup → Room & apps → System → \"Run MySimCaddie as administrator\".", index, StepState.Warning);
            else if (!quiet && !optional)
                Report(_phase, $"Couldn't find \"{button}\" in {window} — press it yourself (details are in the log)", index, StepState.Warning);
        }
        catch (OperationCanceledException)
        {
            // session ended
        }
        catch (Exception ex)
        {
            Log.Error("Auto-click failed", ex);
        }
    }

    private async Task CleanupAsync(DisplayInfo? previousPrimary)
    {
        Report(SessionPhase.Ending, "Tidying up…");

        var toClose = _profile.Steps.Where(s => s.CloseOnEnd).Select(s => s.App)
            .Concat(_profile.AlsoCloseOnEnd)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var id in toClose)
        {
            if (_cfg.Apps.TryGetValue(id, out var app))
                await ProcessTools.CloseAsync(app.EffectiveProcessName, TimeSpan.FromSeconds(6));
        }

        if (_profile.RestorePrimaryOnEnd && previousPrimary is not null)
        {
            var again = DisplayService.Resolve(previousPrimary.ToMatch(), DisplayService.GetDisplays());
            if (again is not null) DisplayService.SetPrimary(again);
        }
    }

    private SessionPhase _phase = SessionPhase.Starting;

    /// <summary>Cancels helpers (auto-click) when the session ends for any reason.</summary>
    private CancellationTokenSource _background = new();

    private void Report(SessionPhase phase, string message, int? step = null, StepState? state = null)
    {
        _phase = phase;
        Log.Info($"[{phase}] {message}");
        _progress.Report(new RunnerUpdate(phase, message, step, state));
    }
}
