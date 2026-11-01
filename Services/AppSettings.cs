using System.Text.Json;

namespace EverySearch;

/// <summary>User settings, stored as JSON in %AppData%\EverySearch\settings.json (edit it from the palette with ">settings").</summary>
public sealed class AppSettings
{
    public string Hotkey { get; set; } = "Ctrl+Alt+Space";
    /// <summary>"system", "dark" or "light".</summary>
    public string Theme { get; set; } = "system";
    /// <summary>Folders or drives to index. Empty means every fixed drive.</summary>
    public List<string> Roots { get; set; } = new();
    /// <summary>Folder names (or full paths) that are never indexed.</summary>
    public List<string> Excludes { get; set; } = new() { "$Recycle.Bin", "System Volume Information", "$WinREAgent" };
    public int RescanMinutes { get; set; } = 30;

    // Last-used search options, restored on the next launch.
    public string Mode { get; set; } = nameof(MatchMode.Contains);
    public bool MatchPath { get; set; }
    public bool CaseSensitive { get; set; }
    /// <summary>Off by default: results skip hidden/system files and Windows, Program Files, AppData, node_modules and similar.</summary>
    public bool IncludeSystem { get; set; }
    public List<string> Types { get; set; } = new();
    public string Extensions { get; set; } = "";
    /// <summary>Drives/folders the search is limited to. Empty means the entire PC.</summary>
    public List<string> Scope { get; set; } = new();
    /// <summary>Named scopes the user saved, e.g. "Projects" -> D:\projects, E:\work.</summary>
    public Dictionary<string, List<string>> SavedScopes { get; set; } = new();
    public bool FirstRunDone { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EverySearch");
    public static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch { /* a corrupt file falls back to defaults and is rewritten on the next save */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* settings are a convenience; never crash over them */ }
    }
}
