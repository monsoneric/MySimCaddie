using System.Text.Json;
using System.Text.Json.Serialization;

namespace MySimCaddie.Core.Config;

/// <summary>Loads/saves %APPDATA%\MySimCaddie\config.json, creating defaults on first run.</summary>
public static class ConfigStore
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MySimCaddie");

    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>True when config.json didn't exist and defaults were written (first launch).</summary>
    public static bool IsFirstRun { get; private set; }

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                IsFirstRun = true;
                var fresh = DefaultConfig.Create();
                Save(fresh);
                Log.Info($"Created default config at {ConfigPath}");
                return fresh;
            }

            var json = File.ReadAllText(ConfigPath);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? DefaultConfig.Create();
            Normalize(cfg);
            return cfg;
        }
        catch (Exception ex)
        {
            // A hand-edit typo shouldn't brick the room: keep the bad file, run on defaults.
            Log.Error("config.json could not be read; using defaults", ex);
            try
            {
                File.Copy(ConfigPath, ConfigPath + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
            }
            catch { /* best effort */ }

            LastLoadError = ex.Message;
            return DefaultConfig.Create();
        }
    }

    public static string? LastLoadError { get; private set; }

    public static void Save(AppConfig cfg)
    {
        Directory.CreateDirectory(DataDirectory);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, JsonOptions));
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    /// <summary>Re-key dictionaries case-insensitively and fill nulls from hand-edited JSON.</summary>
    private static void Normalize(AppConfig cfg)
    {
        cfg.Apps = new Dictionary<string, AppDefinition>(cfg.Apps ?? new(), StringComparer.OrdinalIgnoreCase);
        cfg.Displays = new Dictionary<string, DisplayMatch>(cfg.Displays ?? new(), StringComparer.OrdinalIgnoreCase);
        cfg.Profiles ??= new();
        cfg.QuickLinks ??= new();
        foreach (var p in cfg.Profiles)
        {
            p.Steps ??= new();
            p.AlsoCloseOnEnd ??= new();
        }
    }
}
