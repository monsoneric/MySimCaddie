using MySimCaddie.Core.Config;

namespace MySimCaddie.Core;

/// <summary>
/// Tiny thread-safe daily file logger. Logs live in %APPDATA%\MySimCaddie\logs.
/// Attach the latest log when reporting a problem.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string LogDirectory => Path.Combine(ConfigStore.DataDirectory, "logs");

    public static event Action<string>? MessageLogged;

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level} {message}";
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(Path.Combine(LogDirectory, $"{DateTime.Now:yyyy-MM-dd}.log"), line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never take the launcher down.
        }

        MessageLogged?.Invoke(line);
    }
}
