namespace MySimCaddie.Core.Config;

/// <summary>
/// First-run defaults for JuiceBoxGuy's Hack Shack: GSPro (+ GSPro Connect for the Bushnell Launch Pro)
/// on the projector, SimTuner cameras on the 27" monitor, launcher on the TV.
/// </summary>
public static class DefaultConfig
{
    public const string GsproId = "gspro";
    public const string GspConnectId = "gspconnect";
    public const string SimTunerId = "simtuner";

    public static AppConfig Create()
    {
        var cfg = new AppConfig { Version = ConfigStore.CurrentVersion, BackgroundLogoOpacity = 0.9 };

        cfg.Apps[GsproId] = new AppDefinition
        {
            Name = "GSPro",
            Path = @"C:\GSProV1\GSPLauncher.exe",
            ProcessName = "GSPLauncher",
            Icon = "E7C1", // flag
        };

        // GSPro starts GSPro Connect itself; it's here so the status bar shows the BLP link
        // and so it gets tidied up when a round ends.
        cfg.Apps[GspConnectId] = new AppDefinition
        {
            Name = "GSPro Connect (BLP)",
            Path = @"C:\GSProV1\Core\GSPC\GSPconnect.exe",
            ProcessName = "GSPconnect",
            Icon = "E701", // wifi
            ShowInQuickLaunch = false,
        };

        cfg.Apps[SimTunerId] = new AppDefinition
        {
            Name = "SimTuner",
            Path = "", // set on first run via Setup → Browse
            Icon = "E714", // video
        };

        cfg.Profiles.Add(new Profile
        {
            Id = "gspro-round",
            Name = "GSPro Round",
            Subtitle = "Cameras on monitor · GSPro on projector",
            Icon = "E7C1",
            PrimaryDisplay = DisplayRoles.Projector,
            SessionProcess = "GSPro",
            AlsoCloseOnEnd = { GspConnectId },
            Steps =
            {
                new LaunchStep
                {
                    App = SimTunerId,
                    Display = DisplayRoles.Monitor,
                    Window = WindowMode.Maximize,
                    WaitTimeoutSeconds = 45,
                    DelayAfterSeconds = 2,
                    CloseOnEnd = true,
                },
                new LaunchStep
                {
                    App = GsproId,
                    // GSPLauncher hands off to GSPro.exe once you press Play.
                    WaitForProcess = "GSPro",
                    WaitInBackground = true,
                    AutoClickWindow = GsproPreset.Window,
                    AutoClickButton = GsproPreset.Button,
                    AutoClick2Window = GsproPreset.ConnectWindow,
                    AutoClick2Button = GsproPreset.ConnectButton,
                },
            },
        });

        cfg.Profiles.Add(new Profile
        {
            Id = "gspro-only",
            Name = "GSPro Quick Play",
            Subtitle = "GSPro only, no cameras",
            Icon = "E768",
            AccentColor = "#0EA5E9",
            PrimaryDisplay = DisplayRoles.Projector,
            SessionProcess = "GSPro",
            AlsoCloseOnEnd = { GspConnectId },
            Steps =
            {
                new LaunchStep
                {
                    App = GsproId, WaitForProcess = "GSPro", WaitInBackground = true,
                    AutoClickWindow = GsproPreset.Window, AutoClickButton = GsproPreset.Button,
                    AutoClick2Window = GsproPreset.ConnectWindow, AutoClick2Button = GsproPreset.ConnectButton,
                },
            },
        });

        cfg.Profiles.Add(new Profile
        {
            Id = "swing-cams",
            Name = "Swing Cameras",
            Subtitle = "SimTuner on the 27\" monitor",
            Icon = "E714",
            AccentColor = "#F59E0B",
            // No session process: launch, place the window, and return to the home screen.
            Steps =
            {
                new LaunchStep
                {
                    App = SimTunerId,
                    Display = DisplayRoles.Monitor,
                    Window = WindowMode.Maximize,
                    WaitTimeoutSeconds = 45,
                },
            },
        });

        cfg.QuickLinks.Add(new QuickLink { Name = "Swing Videos", Target = @"%USERPROFILE%\Videos", Icon = "E8B7" });
        cfg.QuickLinks.Add(new QuickLink { Name = "SGT", Target = "https://simulatorgolftour.com", Icon = "E774" });

        return cfg;
    }
}
