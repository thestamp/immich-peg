using System.Text.Json;
using System.Text.RegularExpressions;
using ImmichPeg.Models;

namespace ImmichPeg.Services;

public partial class FlickrSyncEngine
{
    private readonly SyncConfigService _configService;
    private readonly FlickrClient _flickr;
    private readonly ImmichClient _immich;
    private readonly SyncConfig _config;

    [GeneratedRegex(@"""immich_id""\s*:\s*""([a-f0-9-]+)""")]
    private static partial Regex ImmichIdRegex();

    public FlickrSyncEngine(SyncConfigService configService, FlickrClient flickr, SyncConfig config)
    {
        _configService = configService;
        _flickr = flickr;
        _config = config;
        _immich = new ImmichClient(config.Main.Url, config.Main.ApiKey);
    }

    public async Task<Dictionary<string, object>> RunSyncAsync()
    {
        var stats = new Dictionary<string, object>
        {
            ["albums_synced"] = 0, ["photos_added"] = 0, ["photos_skipped"] = 0,
            ["photos_deleted"] = 0, ["albums_created"] = 0, ["errors"] = new List<string>()
        };

        try
        {
            Console.WriteLine($"[FlickrSync] Fetching shared albums from {_config.Main.Url}...");
            var sharedAlbums = await _immich.GetAllAlbumsAsync(true);
            Console.WriteLine($"[FlickrSync] Found {sharedAlbums.Count} shared albums");

            // Track which Immich albums exist (for detecting removals)
            var currentAlbumIds = new HashSet<string>();

            foreach (var album in sharedAlbums)
            {
                try
                {
                    var albumId = album.GetProperty("id").GetString()!;
                    var albumName = album.GetProperty("albumName").GetString()!;
                    currentAlbumIds.Add(albumId);
                    var result = await SyncAlbumToFlickrAsync(albumId, albumName);
                    stats["albums_synced"] = (int)stats["albums_synced"] + 1;
                    stats["photos_added"] = (int)stats["photos_added"] + (int)result["added"];
                    stats["photos_skipped"] = (int)stats["photos_skipped"] + (int)result["skipped"];
                    stats["photos_deleted"] = (int)stats["photos_deleted"] + (int)result["deleted"];
                    if ((bool)result["album_created"]) stats["albums_created"] = (int)stats["albums_created"] + 1;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[FlickrSync] Album error: {ex}");
                    ((List<string>)stats["errors"]).Add($"Error: {ex.Message}");
                }
            }

            // Detect removed albums (in config but no longer in Immich)
            await CleanupRemovedAlbums(currentAlbumIds);
        }
        catch (Exception ex) { Console.WriteLine($"[FlickrSync] Fatal error: {ex}"); }
        finally
        {
            _configService.Save(_config);
        }

        return stats;
    }

    private async Task CleanupRemovedAlbums(HashSet<string> currentAlbumIds)
    {
        var removed = _config.FlickredAlbums.Keys
            .Where(id => !currentAlbumIds.Contains(id))
            .ToList();

        foreach (var albumId in removed)
        {
            try
            {
                var name = _config.SyncedAlbums.TryGetValue(albumId, out var sa) ? sa.AlbumName : albumId;
                Console.WriteLine($"[FlickrSync] Album removed from Immich: {name}");

                // Try to delete the Flickr photoset
                if (_config.FlickredAlbums.TryGetValue(albumId, out var photosetId))
                {
                    try
                    {
                        await _flickr.DeletePhotosetAsync(photosetId);
                        Console.WriteLine($"[FlickrSync] Deleted Flickr photoset {photosetId}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[FlickrSync] Could not delete Flickr photoset: {ex.Message}");
                    }
                    _config.FlickredAlbums.Remove(albumId);
                }

                _config.SyncedAlbums.Remove(albumId);
                _configService.AddRecentAsset(_config, name, "", "removed", "album");
                _configService.Save(_config);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FlickrSync] Error cleaning up removed album: {ex}");
            }
        }
    }

    private async Task<Dictionary<string, object>> SyncAlbumToFlickrAsync(string albumId, string albumName)
    {
        var result = new Dictionary<string, object>
        {
            ["added"] = 0, ["skipped"] = 0, ["deleted"] = 0, ["album_created"] = false
        };

        // === STEP 1: Find or create the Flickr photoset ===
        string? photosetId = null;

        // 1a: Config mapping
        if (_config.FlickredAlbums.TryGetValue(albumId, out var existingId))
        {
            try { await _flickr.GetPhotosetPhotoIdsAsync(existingId); photosetId = existingId; }
            catch
            {
                Console.WriteLine($"[FlickrSync] Cached photoset {existingId} gone, re-locating...");
                _config.FlickredAlbums.Remove(albumId);
            }
        }

        // 1b: Search Flickr album descriptions for immich_id
        if (photosetId == null)
            photosetId = await FindPhotosetByImmichIdAsync(albumId);

        // 1c: Fallback — title search
        if (photosetId == null)
        {
            photosetId = await _flickr.FindPhotosetByTitleAsync(albumName);
            if (photosetId != null)
                Console.WriteLine($"[FlickrSync] Found by title: {photosetId} for '{albumName}'");
        }

        if (photosetId == null)
            Console.WriteLine($"[FlickrSync] Will create new photoset for '{albumName}'");

        // === STEP 2: Get existing photo titles (for dedup) ===
        HashSet<string> existingTitles = new();
        if (photosetId != null)
            existingTitles = await _flickr.GetPhotosetPhotoTitlesAsync(photosetId);

        // === STEP 3: Get Immich assets ===
        var assets = await _immich.GetAlbumAssetsAsync(albumId);
        var assetTitles = new HashSet<string>();
        foreach (var asset in assets)
        {
            var name = asset.TryGetProperty("originalFileName", out var on) ? on.GetString() ?? "" : "";
            if (!string.IsNullOrEmpty(name))
                assetTitles.Add(Path.GetFileNameWithoutExtension(name));
        }

        // === STEP 4: Upload new ===
        foreach (var asset in assets)
        {
            var originalName = asset.TryGetProperty("originalFileName", out var on2) ? on2.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(originalName)) continue;

            var title = Path.GetFileNameWithoutExtension(originalName);
            if (existingTitles.Contains(title))
            {
                result["skipped"] = (int)result["skipped"] + 1;
                continue;
            }

            try
            {
                var assetId = asset.GetProperty("id").GetString()!;
                var assetFile = await _immich.DownloadAssetAsync(assetId);

                var photoId = await _flickr.UploadPhotoAsync(assetFile, originalName, title,
                    $"From Immich album: {albumName}");
                try { File.Delete(assetFile); } catch { }

                result["added"] = (int)result["added"] + 1;
                _config.FlickrPhotosUploaded++;
                existingTitles.Add(title);

                if (photosetId == null)
                {
                    var desc = MakeFlickrDescription(albumId);
                    photosetId = await _flickr.CreatePhotosetAsync(albumName, photoId, desc);
                    _config.FlickredAlbums[albumId] = photosetId;
                    result["album_created"] = true;
                    Console.WriteLine($"[FlickrSync] Created photoset {photosetId}");
                }
                else
                {
                    await _flickr.AddPhotoToPhotosetAsync(photosetId, photoId);
                }

                _configService.AddRecentAsset(_config, originalName, albumName, "added", "photo");
                _configService.Save(_config);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Flickr upload failed: {originalName}: {ex}");
            }
        }

        // === STEP 5: Delete photos no longer in Immich ===
        if (photosetId != null && existingTitles.Count > 0)
        {
            var toDelete = existingTitles.Except(assetTitles).ToList();
            foreach (var title in toDelete)
            {
                try
                {
                    // We need the photo ID, search by title in the photoset
                    var photoIds = await _flickr.GetPhotosetPhotoIdsByTitleAsync(photosetId, title);
                    foreach (var pid in photoIds)
                    {
                        try
                        {
                            await _flickr.DeletePhotoAsync(pid);
                            result["deleted"] = (int)result["deleted"] + 1;
                            _config.FlickrPhotosDeleted++;
                            _configService.AddRecentAsset(_config, title, albumName, "deleted", "photo");
                            Console.WriteLine($"[FlickrSync] Deleted photo: {title}");
                        }
                        catch (Exception dex)
                        {
                            Console.Error.WriteLine($"[FlickrSync] Delete photo failed: {title}: {dex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[FlickrSync] Find photo for delete failed: {title}: {ex.Message}");
                }
            }
        }

        // === STEP 6: Update Flickr description with immich_id (no longer write to Immich) ===
        if (photosetId != null)
        {
            var flickrDesc = MakeFlickrDescription(albumId);
            try
            {
                await _flickr.EditPhotosetMetaAsync(photosetId, albumName, flickrDesc);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FlickrSync] Failed to update Flickr description: {ex.Message}");
            }

            _config.FlickredAlbums[albumId] = photosetId;
            _config.FlickrAlbumsSynced = _config.FlickredAlbums.Count;

            // Track synced album stats
            _config.SyncedAlbums[albumId] = new SyncedAlbum
            {
                AlbumName = albumName,
                AssetCount = assets.Count,
                TotalAssets = assets.Count,
                LastSynced = DateTime.UtcNow.ToString("o")
            };
        }

        return result;
    }

    /// <summary>
    /// Search all Flickr photosets for one whose description contains this immich_id.
    /// </summary>
    private async Task<string?> FindPhotosetByImmichIdAsync(string immichId)
    {
        try
        {
            // Use the flickr client to iterate all photosets
            for (int page = 1; page <= 10; page++)
            {
                var all = await _flickr.GetPhotosetListPageAsync(page, 50);
                if (all.Count == 0) break;
                foreach (var (psId, title, desc) in all)
                {
                    var m = ImmichIdRegex().Match(desc);
                    if (m.Success && m.Groups[1].Value == immichId)
                    {
                        Console.WriteLine($"[FlickrSync] Found photoset by immich_id: {psId} -> {immichId}");
                        return psId;
                    }
                }
                if (all.Count < 50) break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FlickrSync] FindPhotosetByImmichId error: {ex.Message}");
        }
        return null;
    }

    private static string MakeFlickrDescription(string immichAlbumId)
    {
        return $"{{\"immich_id\":\"{immichAlbumId}\"}}";
    }
}
