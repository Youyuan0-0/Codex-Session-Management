using System.Text.Json;

namespace CodexSessionHotSync;

internal sealed record AppSettings
{
    public string? LastCodexHome { get; init; }
    public string? TargetProvider { get; init; }
    public bool IncludeArchived { get; init; } = true;
    public string? LastExportDirectory { get; init; }
    public string? BackupRootDirectory { get; init; }
    public int WindowWidth { get; init; } = 1180;
    public int WindowHeight { get; init; } = 780;
    public int WindowDpi { get; init; }
}

internal sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public AppSettingsStore()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CodexSessionHotSync");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings { WindowDpi = 96 };
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings { WindowDpi = 96 };
        }
    }

    public void Save(AppSettings settings)
    {
        string tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tempPath, _path, true);
    }
}
