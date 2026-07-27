using System.Text.Json;
using ImmichPeg.Models;

namespace ImmichPeg.Services;

public class SyncConfigService
{
    private const string ConfigPath = "/data/config.json";
    private const int RecentAssetsMax = 20;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public SyncConfig Load()
    {
        if (!File.Exists(ConfigPath))
            return new SyncConfig();

        var json = File.ReadAllText(ConfigPath);
        return JsonSerializer.Deserialize<SyncConfig>(json, JsonOpts) ?? new SyncConfig();
    }

    public void Save(SyncConfig config)
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(config, JsonOpts);
        File.WriteAllText(ConfigPath, json);
    }

    public void AddRecentAsset(SyncConfig config, string filename, string albumName)
    {
        config.RecentAssets.Insert(0, new RecentAsset
        {
            Filename = filename,
            AlbumName = albumName,
            Timestamp = DateTime.UtcNow.ToString("o")
        });
        if (config.RecentAssets.Count > RecentAssetsMax)
            config.RecentAssets = config.RecentAssets.Take(RecentAssetsMax).ToList();
    }
}
