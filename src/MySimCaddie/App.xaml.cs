using System.Windows;
using System.Windows.Threading;
using MySimCaddie.Core;
using MySimCaddie.Core.Config;
using MySimCaddie.Core.Displays;
using MySimCaddie.Core.Platform;

namespace MySimCaddie;

public partial class App : Application
{
    private const string InstanceName = "Local\\MySimCaddie.SingleInstance";
    private const string ActivateEventName = "Local\\MySimCaddie.Activate";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;

    public static bool LaunchedAtLogin { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        bool fromTask = e.Args.Any(a => a.Equals(StartupRegistration.FromTaskArg, StringComparison.OrdinalIgnoreCase));

        // One launcher per room: a second launch just brings the first one forward.
        // When we were just relaunched as administrator, the old copy may still be closing — give it a moment.
        if (!AcquireInstanceLock(waitSeconds: fromTask ? 8 : 0))
        {
            try { EventWaitHandle.OpenExisting(ActivateEventName).Set(); } catch { /* not reachable (e.g. other copy is admin) */ }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Unobserved task", args.Exception); args.SetObserved(); };

        base.OnStartup(e);
        bool elevated = StartupRegistration.IsElevated;
        LaunchedAtLogin = e.Args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase))
                          || (fromTask && StartupRegistration.WasStartedAtSignIn());
        Log.Info($"MySimCaddie {typeof(App).Assembly.GetName().Version} starting{(LaunchedAtLogin ? " (at login)" : "")}, administrator: {(elevated ? "yes" : "no")}");

        AutoClicker.FallbackClick = Services.UiaClicker.Click;

        var config = ConfigStore.Load();

        if (Environment.ProcessPath is { } exe)
        {
            if (StartupRegistration.Sync(config, exe)) ConfigStore.Save(config);

            // Administrator mode: hand over to an elevated copy and bow out.
            if (config.RunElevated && !elevated && RelaunchElevated(exe))
                return;
        }

        // First launch: take a guess at Projector / TV / Monitor from the monitor names.
        if (ConfigStore.IsFirstRun || config.Displays.Count == 0)
        {
            var guessed = DisplayService.GuessRoles(DisplayService.GetDisplays());
            foreach (var kv in guessed) config.Displays[kv.Key] = kv.Value;
            ConfigStore.Save(config);
        }

        if (!string.IsNullOrWhiteSpace(config.PrimaryDisplayAtStartup))
        {
            var target = DisplayService.Resolve(config, config.PrimaryDisplayAtStartup);
            if (target is { IsPrimary: false }) DisplayService.SetPrimary(target);
        }

        var window = new MainWindow(config, showSetup: ConfigStore.IsFirstRun);
        MainWindow = window;
        window.Show();

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var listener = new Thread(() =>
        {
            while (_activateEvent.WaitOne())
                Dispatcher.InvokeAsync(() => window.BringToFront());
        }) { IsBackground = true, Name = "ActivateListener" };
        listener.Start();
    }

    private bool AcquireInstanceLock(int waitSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
        while (true)
        {
            try
            {
                var m = new Mutex(true, InstanceName, out bool isFirst);
                if (isFirst)
                {
                    _instanceMutex = m;
                    return true;
                }

                m.Dispose();
            }
            catch (UnauthorizedAccessException)
            {
                // Held by a copy running as administrator.
            }

            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(250);
        }
    }

    /// <summary>Restart as administrator. On success this copy shuts down; on failure it keeps running normally.</summary>
    public bool RelaunchElevated(string exe)
    {
        _instanceMutex?.Dispose();
        _instanceMutex = null;

        if (StartupRegistration.RelaunchElevated(exe))
        {
            _relaunching = true;
            Shutdown();
            return true;
        }

        AcquireInstanceLock(waitSeconds: 0);
        return false;
    }

    private bool _relaunching;

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        (MainWindow as MainWindow)?.ShowToast($"Something went wrong: {e.Exception.Message}");
        e.Handled = true; // keep the room running
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info(_relaunching ? "MySimCaddie handing over to the administrator copy" : "MySimCaddie exiting");
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
