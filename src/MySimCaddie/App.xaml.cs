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
        // One launcher per room: a second launch just brings the first one forward.
        _instanceMutex = new Mutex(true, InstanceName, out bool isFirst);
        if (!isFirst)
        {
            try { EventWaitHandle.OpenExisting(ActivateEventName).Set(); } catch { /* first instance still starting */ }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Unobserved task", args.Exception); args.SetObserved(); };

        base.OnStartup(e);
        LaunchedAtLogin = e.Args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        Log.Info($"MySimCaddie {typeof(App).Assembly.GetName().Version} starting{(LaunchedAtLogin ? " (at login)" : "")}");

        AutoClicker.FallbackClick = Services.UiaClicker.Click;

        var config = ConfigStore.Load();

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

        if (Environment.ProcessPath is { } exe)
            StartupRegistration.Set(config.StartWithWindows, exe);

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

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        (MainWindow as MainWindow)?.ShowToast($"Something went wrong: {e.Exception.Message}");
        e.Handled = true; // keep the room running
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("MySimCaddie exiting");
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
