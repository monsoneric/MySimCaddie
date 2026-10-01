using System.ComponentModel;
using System.Diagnostics;
using MySimCaddie.Core.Config;

namespace MySimCaddie.Core.Launch;

public static class AppLauncher
{
    /// <summary>Why an app can't be launched, or null if it looks good.</summary>
    public static string? Problem(AppDefinition app)
    {
        if (string.IsNullOrWhiteSpace(app.Path)) return $"{app.Name}: path not set";
        if (!File.Exists(app.ExpandedPath)) return $"{app.Name}: not found at {app.ExpandedPath}";
        return null;
    }

    /// <summary>Start an app. Returns null on success, or a human-readable error.</summary>
    public static string? Start(AppDefinition app)
    {
        var problem = Problem(app);
        if (problem is not null) return problem;

        var path = app.ExpandedPath;
        var psi = new ProcessStartInfo(path)
        {
            Arguments = Environment.ExpandEnvironmentVariables(app.Arguments ?? ""),
            WorkingDirectory = Path.GetDirectoryName(path) ?? "",
            UseShellExecute = true,
        };
        if (app.RunAsAdmin) psi.Verb = "runas";

        try
        {
            Log.Info($"Starting {app.Name}: \"{path}\" {psi.Arguments}{(app.RunAsAdmin ? " [admin]" : "")}");
            Process.Start(psi)?.Dispose();
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return $"{app.Name}: the admin (UAC) prompt was declined";
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to start {app.Name}", ex);
            return $"{app.Name}: {ex.Message}";
        }
    }

    /// <summary>Open a folder, file or URL with its default handler.</summary>
    public static string? Open(string target)
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(target ?? "");
            Process.Start(new ProcessStartInfo(expanded) { UseShellExecute = true })?.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open {target}", ex);
            return ex.Message;
        }
    }
}
