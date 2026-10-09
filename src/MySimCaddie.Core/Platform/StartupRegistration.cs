using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text;
using Microsoft.Win32;
using MySimCaddie.Core.Config;

namespace MySimCaddie.Core.Platform;

/// <summary>
/// Start-with-Windows and "run as administrator".
/// Normal mode: HKCU\...\Run (per user, no admin needed).
/// Administrator mode: a Task Scheduler task with highest privileges, so MySimCaddie starts elevated at sign-in
/// and can relaunch itself elevated without a UAC prompt. Creating/removing the task needs one UAC prompt.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MySimCaddie";
    public const string TaskName = "MySimCaddie";
    public const string FromTaskArg = "--from-task";

    private static string ManualRelaunchFlag => Path.Combine(ConfigStore.DataDirectory, "relaunch.flag");

    public static bool IsElevated => AutoClicker.IsCurrentProcessElevated();

    /// <summary>Bring Windows in line with the config. Returns true if the config changed (needs saving).</summary>
    public static bool Sync(AppConfig cfg, string exePath)
    {
        try
        {
            if (cfg.RunElevated)
            {
                SetRunKey(false, exePath);
                var desired = $"{exePath}|{cfg.StartWithWindows}";
                if (cfg.RegisteredTask == desired) return false;

                if (CreateTask(exePath, cfg.StartWithWindows))
                {
                    cfg.RegisteredTask = desired;
                    return true;
                }

                Log.Warn("Administrator mode is on but the startup task couldn't be created");
                return false;
            }

            SetRunKey(cfg.StartWithWindows, exePath);
            if (!string.IsNullOrEmpty(cfg.RegisteredTask))
            {
                if (RunSchtasks($"/Delete /TN \"{TaskName}\" /F", elevate: !IsElevated) is 0 or 1)
                {
                    cfg.RegisteredTask = "";
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not update startup settings", ex);
        }

        return false;
    }

    /// <summary>
    /// Restart MySimCaddie as administrator: through the task (no prompt) if it exists, otherwise with a UAC prompt.
    /// Call after releasing the single-instance lock. Returns false if it couldn't (e.g. the prompt was declined).
    /// </summary>
    public static bool RelaunchElevated(string exePath)
    {
        try
        {
            Directory.CreateDirectory(ConfigStore.DataDirectory);
            File.WriteAllText(ManualRelaunchFlag, DateTime.UtcNow.ToString("O"));

            if (RunSchtasks($"/Run /TN \"{TaskName}\"", elevate: false) == 0)
            {
                Log.Info("Relaunching as administrator via the startup task");
                return true;
            }

            Log.Info("Relaunching as administrator with a UAC prompt");
            Process.Start(new ProcessStartInfo(exePath, FromTaskArg) { UseShellExecute = true, Verb = "runas" })?.Dispose();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Warn("Administrator relaunch: UAC prompt declined");
        }
        catch (Exception ex)
        {
            Log.Error("Administrator relaunch failed", ex);
        }

        TryDelete(ManualRelaunchFlag);
        return false;
    }

    /// <summary>Started by the task: was it the sign-in trigger (true) or a manual relaunch (false)?</summary>
    public static bool WasStartedAtSignIn()
    {
        try
        {
            if (File.Exists(ManualRelaunchFlag) &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(ManualRelaunchFlag) < TimeSpan.FromMinutes(2))
            {
                TryDelete(ManualRelaunchFlag);
                return false;
            }
        }
        catch { /* treat as sign-in */ }

        return true;
    }

    private static void SetRunKey(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{exePath}\" --autostart");
        else if (key.GetValue(ValueName) is not null)
            key.DeleteValue(ValueName);
    }

    private static bool CreateTask(string exePath, bool atSignIn)
    {
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var trigger = atSignIn
            ? $"<LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId><Delay>PT3S</Delay></LogonTrigger>"
            : "";

        // Priority 4 = normal. The Task Scheduler default (7) would run MySimCaddie — and GSPro, which inherits it — below normal.
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts MySimCaddie with administrator rights.</Description></RegistrationInfo>
              <Triggers>{trigger}</Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exePath)}</Command>
                  <Arguments>{FromTaskArg}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        Directory.CreateDirectory(ConfigStore.DataDirectory);
        var xmlPath = Path.Combine(ConfigStore.DataDirectory, "startup-task.xml");
        File.WriteAllText(xmlPath, xml, Encoding.Unicode);
        try
        {
            var code = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F", elevate: !IsElevated);
            Log.Info($"Create startup task (sign-in: {atSignIn}) → exit code {code}");
            return code == 0;
        }
        finally
        {
            TryDelete(xmlPath);
        }
    }

    /// <returns>schtasks exit code, or -1 if it couldn't run (including a declined UAC prompt).</returns>
    private static int RunSchtasks(string args, bool elevate)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = elevate,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (elevate) psi.Verb = "runas";

            using var p = Process.Start(psi);
            if (p is null) return -1;
            if (!p.WaitForExit(20000)) return -1;
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Warn("UAC prompt declined");
            return -1;
        }
        catch (Exception ex)
        {
            Log.Error($"schtasks {args} failed", ex);
            return -1;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
