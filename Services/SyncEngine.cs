using System.Text.Json;
using ImmichPeg.Models;

namespace ImmichPeg.Services;

public class SyncEngine
{
    private readonly ImmichClient _main;
    private readonly ImmichClient _public;
    private readonly SyncConfigService _configService;
    private CancellationTokenSource? _cts;

    public SyncConfig Config { get; private set; }
    public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;
    public ImmichClient Main => _main;
    public ImmichClient Public => _public;

    public SyncEngine(SyncConfigService configService, ImmichClient main, ImmichClient @public)
    {
        _configService = configService;
        _main = main;
        _public = @public;
        Config = configService.Load();
    }

    public void Cancel()
    {
        _cts?.Cancel();
    }

    private void CheckCancelled()
    {
        _cts?.Token.ThrowIfCancellationRequested();
    }

    public async Task<Dictionary<string, bool>> HealthCheckAsync()
    {
        return new()
        {
            ["main"] = await _main.PingAsync(),
            ["public"] = await _public.PingAsync()
        };
    }

    public async Task<Dictionary<string, object>> RunPublishAsync()
    {
        _cts = new CancellationTokenSource();
        Config.LastSyncStatus = "running";
        Config.LastSync = DateTime.UtcNow.ToString("o");
        _configService.Save(Config);

        var stats = new Dictionary<string, object>
        {
            ["albums_published"] = 0, ["slugs_replicated"] = 0, ["errors"] = new List<string>()
        };

        try
        {
            var sharedAlbums = await _main.GetAllAlbumsAsync(true);
            var sharedIds = new HashSet<string>();
            Config.TotalSharedAlbums = sharedAlbums.Count;

            foreach (var album in sharedAlbums)
            {
                CheckCancelled();
                try
                {
                    var albumId = album.GetProperty("id").GetString()!;
                    var albumName = album.GetProperty("albumName").GetString()!;
                    sharedIds.Add(albumId);

                    Config.SyncedAlbums.TryGetValue(albumId, out var existing);
                    var existingDict = existing != null ? new Dictionary<string, string> {
                        ["public_album_id"] = existing.PublicAlbumId,
                        ["album_name"] = existing.AlbumName,
                        ["asset_count"] = existing.AssetCount.ToString()
                    } : null;

                    var result = await SyncMetadataAsync(album, existingDict);
                    stats["albums_published"] = (int)stats["albums_published"] + 1;
                    if ((bool)result["slug_replicated"]) stats["slugs_replicated"] = (int)stats["slugs_replicated"] + 1;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    ((List<string>)stats["errors"]).Add($"Error publishing: {ex.Message}");
                }
            }

            // Remove unshared
            foreach (var kv in Config.SyncedAlbums.ToList())
            {
                if (!sharedIds.Contains(kv.Key))
                    await RemoveSyncedAlbumAsync(kv.Key, kv.Value);
            }

            Config.TotalSyncedAlbums = Config.SyncedAlbums.Count;
            Config.LastSyncStatus = ((List<string>)stats["errors"]).Count == 0 ? "success" : "failed";
            Config.LastSyncMessage = $"Published {stats["albums_published"]} albums, {stats["slugs_replicated"]} slugs replicated";
        }
        catch (OperationCanceledException)
        {
            Config.LastSyncStatus = "cancelled";
            Config.LastSyncMessage = "Publish cancelled";
        }
        finally
        {
            Config.LastSync = DateTime.UtcNow.ToString("o");
            _configService.Save(Config);
            _cts = null;
        }
        return stats;
    }

    public async Task<Dictionary<string, object>> RunAssetsAsync()
    {
        _cts = new CancellationTokenSource();
        Config.LastSyncStatus = "running";
        Config.LastSync = DateTime.UtcNow.ToString("o");
        _configService.Save(Config);

        var stats = new Dictionary<string, object>
        {
            ["albums_synced"] = 0, ["assets_copied"] = 0, ["errors"] = new List<string>()
        };

        try
        {
            var sharedAlbums = await _main.GetAllAlbumsAsync(true);
            Config.TotalSharedAlbums = sharedAlbums.Count;

            foreach (var album in sharedAlbums)
            {
                CheckCancelled();
                try
                {
                    Config.SyncedAlbums.TryGetValue(album.GetProperty("id").GetString()!, out var existing);
                    var result = await SyncAssetsAsync(album, existing);
                    stats["albums_synced"] = (int)stats["albums_synced"] + 1;
                    stats["assets_copied"] = (int)stats["assets_copied"] + (int)result["assets_copied"];
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    ((List<string>)stats["errors"]).Add($"Error: {ex.Message}");
                }
            }

            Config.TotalSyncedAlbums = Config.SyncedAlbums.Count;
            Config.LastSyncStatus = ((List<string>)stats["errors"]).Count == 0 ? "success" : "failed";
            Config.LastSyncMessage = $"Synced {stats["albums_synced"]} albums, copied {stats["assets_copied"]} assets";
        }
        catch (OperationCanceledException)
        {
            Config.LastSyncStatus = "cancelled";
            Config.LastSyncMessage = $"Asset sync cancelled (copied {stats["assets_copied"]} so far)";
        }
        finally
        {
            Config.LastSync = DateTime.UtcNow.ToString("o");
            Config.AlbumsSynced += (int)stats["albums_synced"];
            Config.AssetsCopied += (int)stats["assets_copied"];
            _configService.Save(Config);
            _cts = null;
        }
        return stats;
    }

    private async Task<Dictionary<string, bool>> SyncMetadataAsync(JsonElement album, Dictionary<string, string>? existing)
    {
        var result = new Dictionary<string, bool> { ["created"] = false, ["slug_replicated"] = false };
        var albumId = album.GetProperty("id").GetString()!;
        var albumName = album.GetProperty("albumName").GetString()!;

        var publicAlbumId = existing?.GetValueOrDefault("public_album_id");

        if (publicAlbumId != null)
        {
            try { await _public.GetAlbumAsync(publicAlbumId); }
            catch { publicAlbumId = null; }
        }

        if (publicAlbumId == null)
        {
            var created = await _public.CreateAlbumAsync(albumName, album.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "");
            publicAlbumId = created.GetProperty("id").GetString()!;
            result["created"] = true;
        }

        result["slug_replicated"] = await ReplicateShareAsync(albumId, publicAlbumId);

        Config.SyncedAlbums[albumId] = new SyncedAlbum
        {
            PublicAlbumId = publicAlbumId,
            AlbumName = albumName,
            AssetCount = Config.SyncedAlbums.TryGetValue(albumId, out var existingAlbum) ? existingAlbum.AssetCount : 0,
            TotalAssets = Config.SyncedAlbums.TryGetValue(albumId, out var existingAlbum2) ? existingAlbum2.TotalAssets : 0,
            LastSynced = DateTime.UtcNow.ToString("o")
        };

        return result;
    }

    private async Task<Dictionary<string, object>> SyncAssetsAsync(JsonElement album, SyncedAlbum? existing)
    {
        var result = new Dictionary<string, object> { ["assets_copied"] = 0, ["assets_re_added"] = 0 };
        var albumId = album.GetProperty("id").GetString()!;
        var albumName = album.GetProperty("albumName").GetString()!;

        if (!Config.SyncedAlbums.TryGetValue(albumId, out var synced) || string.IsNullOrEmpty(synced.PublicAlbumId))
            return result;

        var publicAlbumId = synced.PublicAlbumId;
        var mainAssets = await _main.GetAlbumAssetsAsync(albumId);

        // Album assets
        var publicAlbumAssets = await _public.GetAlbumAssetsAsync(publicAlbumId);
        var albumFilenames = new Dictionary<string, string>();
        foreach (var a in publicAlbumAssets)
        {
            var name = a.TryGetProperty("originalFileName", out var n) ? n.GetString() ?? "" : "";
            if (!string.IsNullOrEmpty(name)) albumFilenames[name] = a.GetProperty("id").GetString()!;
        }

        // All public assets for self-healing
        var allPublicFilenames = await _public.GetAllAssetFilenamesAsync();

        foreach (var asset in mainAssets)
        {
            CheckCancelled();
            var originalName = asset.TryGetProperty("originalFileName", out var on) ? on.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(originalName)) continue;
            if (albumFilenames.ContainsKey(originalName)) continue;

            try
            {
                if (allPublicFilenames.TryGetValue(originalName, out var publicAssetId))
                {
                    await _public.AddAssetsToAlbumAsync(publicAlbumId, new List<string> { publicAssetId });
                    result["assets_re_added"] = (int)result["assets_re_added"] + 1;
                }
                else
                {
                    var assetId = asset.GetProperty("id").GetString()!;
                    var assetData = await _main.DownloadAssetAsync(assetId);
                    CheckCancelled();
                    var uploadResult = await _public.UploadAssetAsync(assetData, originalName,
                        asset.TryGetProperty("fileCreatedAt", out var fca) ? fca.GetString() ?? "" : "",
                        asset.TryGetProperty("fileModifiedAt", out var fma) ? fma.GetString() ?? "" : "");
                    if (uploadResult.TryGetProperty("id", out var uid))
                    {
                        var newId = uid.GetString()!;
                        await _public.AddAssetsToAlbumAsync(publicAlbumId, new List<string> { newId });
                        result["assets_copied"] = (int)result["assets_copied"] + 1;
                        allPublicFilenames[originalName] = newId;
                    }
                }
                _configService.AddRecentAsset(Config, originalName, albumName);
                _configService.Save(Config);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed: {originalName}: {ex.Message}");
            }
        }

        // Re-fetch public album to get accurate synced count
        var syncedCount = mainAssets.Count;
        try
        {
            var finalPublicAssets = await _public.GetAlbumAssetsAsync(publicAlbumId);
            syncedCount = finalPublicAssets.Count;
        }
        catch { }

        Config.SyncedAlbums[albumId] = new SyncedAlbum
        {
            PublicAlbumId = publicAlbumId,
            AlbumName = albumName,
            AssetCount = syncedCount,
            TotalAssets = mainAssets.Count,
            LastSynced = DateTime.UtcNow.ToString("o")
        };

        return result;
    }

    private async Task<bool> ReplicateShareAsync(string mainAlbumId, string publicAlbumId)
    {
        try
        {
            var mainShare = await _main.GetShareForAlbumAsync(mainAlbumId);
            var mainHasPeg = mainShare.HasValue && (mainShare.Value.TryGetProperty("slug", out var ms) ? ms.GetString() ?? "" : "").StartsWith("peg_");
            var mainSlug = mainHasPeg ? mainShare!.Value.GetProperty("slug").GetString()! : null;

            string? slug = mainSlug;

            if (slug == null)
            {
                var publicShares = await _public.GetAlbumSharesAsync();
                foreach (var share in publicShares)
                {
                    if (share.TryGetProperty("album", out var a) && a.TryGetProperty("id", out var aid) && aid.GetString() == publicAlbumId)
                    {
                        var s = share.TryGetProperty("slug", out var sl) ? sl.GetString() ?? "" : "";
                        if (s.StartsWith("peg_")) { slug = s; break; }
                    }
                }
            }

            slug ??= $"peg_{Convert.ToHexString(Guid.NewGuid().ToByteArray())[..8].ToLower()}";

            var changed = false;
            var desc = mainShare?.TryGetProperty("description", out var d) == true ? d.GetString() ?? "" : "";
            var allowDl = mainShare?.TryGetProperty("allowDownload", out var ad) == true ? ad.GetBoolean() : true;
            var showMeta = mainShare?.TryGetProperty("showMetadata", out var sm) == true ? sm.GetBoolean() : true;

            if (!mainHasPeg)
            {
                if (mainShare.HasValue)
                {
                    try { await _main.DeleteShareAsync(mainShare.Value.GetProperty("id").GetString()!); } catch { }
                }
                await _main.CreateShareAsync(mainAlbumId, slug, desc, allowDl, showMeta);
                changed = true;
            }

            var pubShares = await _public.GetAlbumSharesAsync();
            var pubShare = pubShares.FirstOrDefault(s =>
                s.TryGetProperty("album", out var a) && a.TryGetProperty("id", out var aid) && aid.GetString() == publicAlbumId);
            var pubHasPeg = pubShare.TryGetProperty("slug", out var ps) && (ps.GetString() ?? "").StartsWith("peg_");

            if (!pubHasPeg)
            {
                if (pubShare.ValueKind != JsonValueKind.Undefined)
                {
                    try { await _public.DeleteShareAsync(pubShare.GetProperty("id").GetString()!); } catch { }
                }
                await _public.CreateShareAsync(publicAlbumId, slug, desc, allowDl, showMeta);
                changed = true;
            }

            return changed;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Share replication failed: {ex.Message}");
            return false;
        }
    }

    private async Task RemoveSyncedAlbumAsync(string mainAlbumId, SyncedAlbum info)
    {
        if (!string.IsNullOrEmpty(info.PublicAlbumId))
        {
            try { await _public.DeleteAlbumAsync(info.PublicAlbumId); } catch { }
        }
        Config.SyncedAlbums.Remove(mainAlbumId);
    }

    public async Task<DashboardData> GetDashboardAsync()
    {
        Config = _configService.Load(); // Reload from disk
        var data = new DashboardData
        {
            Health = await HealthCheckAsync(),
            SetupComplete = Config.SetupComplete,
            SyncIntervalMinutes = Config.SyncIntervalMinutes,
            LastSync = Config.LastSync,
            LastSyncStatus = Config.LastSyncStatus,
            LastSyncMessage = Config.LastSyncMessage,
            SyncActive = IsRunning,
            SettingsEnabled = Config.SettingsEnabled,
            DashboardEnabled = Config.DashboardEnabled,
            TotalSharedAlbums = Config.TotalSharedAlbums,
            TotalSyncedAlbums = Config.TotalSyncedAlbums,
            TotalAlbumsSyncedEver = Config.AlbumsSynced,
            TotalAssetsCopiedEver = Config.AssetsCopied,
            TotalAlbumsRemovedEver = Config.AlbumsRemoved,
            SyncedAlbums = Config.SyncedAlbums.Values.ToList(),
            RecentAssets = Config.RecentAssets
        };

        // Permissions
        try
        {
            var mainKey = await _main.GetApiKeyInfoAsync();
            var mainPerms = mainKey.TryGetProperty("permissions", out var mp) ? mp.EnumerateArray().Select(x => x.GetString()!).ToList() : new List<string>();
            data.Permissions = new() {
                ["main"] = new PermissionStatus {
                    Granted = mainPerms,
                    Required = ImmichClient.MainRequiredPermissions.ToList(),
                    Missing = mainPerms.Contains("all") ? new() : ImmichClient.MainRequiredPermissions.Where(p => !mainPerms.Contains(p)).ToList()
                },
                ["public"] = new PermissionStatus { Granted = new() }
            };
        }
        catch { }

        try
        {
            var pubKey = await _public.GetApiKeyInfoAsync();
            var pubPerms = pubKey.TryGetProperty("permissions", out var pp) ? pp.EnumerateArray().Select(x => x.GetString()!).ToList() : new List<string>();
            if (data.Permissions != null)
            {
                data.Permissions["public"] = new PermissionStatus
                {
                    Granted = pubPerms,
                    Required = ImmichClient.PublicRequiredPermissions.ToList(),
                    Missing = pubPerms.Contains("all") ? new() : ImmichClient.PublicRequiredPermissions.Where(p => !pubPerms.Contains(p)).ToList()
                };
            }
        }
        catch { }

        return data;
    }
}
