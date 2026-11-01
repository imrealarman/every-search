using System.Text.Json;

namespace EverySearch;

/// <summary>Remembers which files you open so they rank higher next time. Stored in %AppData%\EverySearch\usage.json.</summary>
public sealed class UsageStore
{
    public sealed class Use
    {
        public int Count { get; set; }
        public long Last { get; set; }
    }

    private const int MaxRemembered = 2000;
    private readonly object gate = new();
    private readonly string file;
    private Dictionary<string, Use> map = new(StringComparer.OrdinalIgnoreCase);

    public UsageStore(string? path = null)
    {
        file = path ?? Path.Combine(AppSettings.Folder, "usage.json");
        try
        {
            if (File.Exists(file))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, Use>>(File.ReadAllText(file));
                if (loaded != null) map = new Dictionary<string, Use>(loaded, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { /* a corrupt file just resets the ranking history */ }
    }

    public void Clear()
    {
        lock (gate)
        {
            map.Clear();
            Save();
        }
    }

    public void Record(string path)
    {
        lock (gate)
        {
            if (!map.TryGetValue(path, out var use)) map[path] = use = new Use();
            use.Count++;
            use.Last = DateTime.UtcNow.Ticks;
            if (map.Count > MaxRemembered)
                map = map.OrderByDescending(kv => kv.Value.Last).Take(MaxRemembered * 3 / 4)
                         .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            Save();
        }
    }

    /// <summary>
    /// How much to pull a result up the list (subtracted from its sort key). Capped below one whole file-type tier
    /// (10^10), so opened files beat better name matches but a system file never beats a document.
    /// </summary>
    public long Bonus(string path)
    {
        lock (gate)
        {
            if (!map.TryGetValue(path, out var use)) return 0;
            long bonus = Math.Min(use.Count, 5) * 1_000_000_000L;
            if (DateTime.UtcNow.Ticks - use.Last < TimeSpan.FromDays(7).Ticks) bonus += 1_000_000_000L;
            return bonus;
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(map));
        }
        catch { /* ranking history is a convenience */ }
    }
}
