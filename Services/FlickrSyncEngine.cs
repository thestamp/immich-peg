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

    // Pattern to extract flickr_id from Immich album description
    // Looks for {"flickr_id":"721777..."} anywhere in the description
    [GeneratedRegex(@"""flickr_id""\s*:\s*""(\d+@N\d+)""")]
    private static partial Regex FlickrIdRegex();

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
            ["albums_synced"] = 0, ["photos_uploaded"] = 0, ["photos_skipped"] = 0, ["errors"] = new List<string>()
        };

        try
        {
            Console.WriteLine($"[FlickrSync] Fetching shared albums from {_config.Main.Url}...");
            var sharedAlbums = await _immich.GetAllAlbumsAsync(true);
            Console.WriteLine($"[FlickrSync] Found {sharedAlbums.Count} shared albums");

            foreach (var album in sharedAlbums)
            {
                try
                {
                    var albumId = album.GetProperty("id").GetString()!;
                    var albumName = album.GetProperty("albumName").GetString()!;
                    var albumDescription = album.TryGetProperty("description", out var desc)
                        ? desc.GetString() ?? ""
                        : "";

                    var result = await SyncAlbumToFlickrAsync(albumId, albumName, albumDescription);
                    stats["albums_synced"] = (int)stats["albums_synced"] + 1;
                    stats["photos_uploaded"] = (int)stats["photos_uploaded"] + (int)result["uploaded"];
                    stats["photos_skipped"] = (int)stats["photos_skipped"] + (int)result["skipped"];
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[FlickrSync] Album error: {ex}");
                    ((List<string>)stats["errors"]).Add($"Error: {ex.Message}");
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"[FlickrSync] Fatal error: {ex}"); }
        finally
        {
            _configService.Save(_config);
        }

        return stats;
    }

    private async Task<Dictionary<string, object>> SyncAlbumToFlickrAsync(
        string albumId, string albumName, string albumDescription)
    {
        var result = new Dictionary<string, object> { ["uploaded"] = 0, ["skipped"] = 0 };

        // === STEP 1: Find or create the Flickr photoset ===
        string? photosetId = null;
        bool isNewPhotoset = false;

        // 1a: Check config-based mapping first
        if (_config.FlickredAlbums.TryGetValue(albumId, out var existingId))
        {
            // Verify it still exists on Flickr
            try
            {
                await _flickr.GetPhotosetPhotoIdsAsync(existingId);
                photosetId = existingId;
                Console.WriteLine($"[FlickrSync] Found photoset from config: {photosetId} for album {albumId}");
            }
            catch
            {
                Console.WriteLine($"[FlickrSync] Cached photoset {existingId} no longer exists, re-locating...");
                _config.FlickredAlbums.Remove(albumId);
            }
        }

        // 1b: Check Immich album description for stored flickr_id
        if (photosetId == null)
        {
            var match = FlickrIdRegex().Match(albumDescription);
            if (match.Success)
            {
                var descPhotosetId = match.Groups[1].Value;
                try
                {
                    await _flickr.GetPhotosetPhotoIdsAsync(descPhotosetId);
                    photosetId = descPhotosetId;
                    _config.FlickredAlbums[albumId] = photosetId;
                    Console.WriteLine($"[FlickrSync] Found photoset from Immich description: {photosetId} for album {albumId}");
                }
                catch
                {
                    Console.WriteLine($"[FlickrSync] Description photoset {descPhotosetId} no longer exists, falling back...");
                }
            }
        }

        // 1c: Fallback — search by title
        if (photosetId == null)
        {
            photosetId = await _flickr.FindPhotosetByTitleAsync(albumName);
            if (photosetId != null)
            {
                Console.WriteLine($"[FlickrSync] Found photoset by title: {photosetId} for '{albumName}'");
                _config.FlickredAlbums[albumId] = photosetId;
            }
        }

        // 1d: Create new photoset if nothing found
        if (photosetId == null)
        {
            isNewPhotoset = true;
            Console.WriteLine($"[FlickrSync] Will create new photoset for '{albumName}' on first upload");
        }

        // === STEP 2: Get existing photo titles (for dedup) ===
        HashSet<string> existingTitles = new();
        if (photosetId != null)
        {
            existingTitles = await _flickr.GetPhotosetPhotoTitlesAsync(photosetId);
            Console.WriteLine($"[FlickrSync] Photoset {photosetId} has {existingTitles.Count} existing photos");
        }

        // === STEP 3: Get Immich assets for this album ===
        var assets = await _immich.GetAlbumAssetsAsync(albumId);
        Console.WriteLine($"[FlickrSync] Album '{albumName}' has {assets.Count} assets");

        // === STEP 4: Upload new photos (skip duplicates by title) ===
        foreach (var asset in assets)
        {
            var originalName = asset.TryGetProperty("originalFileName", out var on)
                ? on.GetString() ?? ""
                : "";
            if (string.IsNullOrEmpty(originalName)) continue;

            var title = Path.GetFileNameWithoutExtension(originalName);

            // Skip if already in the photoset (by title)
            if (existingTitles.Contains(title))
            {
                result["skipped"] = (int)result["skipped"] + 1;
                continue;
            }

            try
            {
                var assetId = asset.GetProperty("id").GetString()!;
                var assetData = await _immich.DownloadAssetAsync(assetId);
                Console.WriteLine($"[FlickrSync] Downloaded {assetId}: {assetData.Length} bytes ({originalName})");

                var photoId = await _flickr.UploadPhotoAsync(assetData, originalName, title,
                    $"From Immich album: {albumName}");

                result["uploaded"] = (int)result["uploaded"] + 1;
                _config.FlickrPhotosUploaded++;
                existingTitles.Add(title); // Track so we don't skip within same batch

                // Create photoset with first photo if needed
                if (photosetId == null)
                {
                    var immichRef = MakeFlickrDescription(albumId);
                    photosetId = await _flickr.CreatePhotosetAsync(albumName, photoId, immichRef);
                    _config.FlickredAlbums[albumId] = photosetId;
                    isNewPhotoset = true;
                    Console.WriteLine($"[FlickrSync] Created photoset {photosetId} with immich_id={albumId}");
                }
                else
                {
                    await _flickr.AddPhotoToPhotosetAsync(photosetId, photoId);
                }

                _configService.AddRecentAsset(_config, originalName, $"Flickr: {albumName}");
                _configService.Save(_config);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Flickr upload failed: {originalName}: {ex}");
            }
        }

        // === STEP 5: Sync cross-references (store IDs in descriptions) ===
        if (photosetId != null)
        {
            // Update Immich album description with flickr_id
            var immichDesc = MakeImmichDescription(photosetId, albumDescription);
            try
            {
                await _immich.UpdateAlbumDescriptionAsync(albumId, immichDesc);
                Console.WriteLine($"[FlickrSync] Updated Immich album {albumId} description with flickr_id={photosetId}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FlickrSync] Failed to update Immich description: {ex.Message}");
            }

            // Update Flickr photoset description with immich_id
            var flickrDesc = MakeFlickrDescription(albumId);
            try
            {
                await _flickr.EditPhotosetMetaAsync(photosetId, albumName, flickrDesc);
                Console.WriteLine($"[FlickrSync] Updated Flickr photoset {photosetId} description with immich_id={albumId}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FlickrSync] Failed to update Flickr description: {ex.Message}");
            }

            _config.FlickredAlbums[albumId] = photosetId;
            _config.FlickrAlbumsSynced = _config.FlickredAlbums.Count;

            // Reset the tracking so it re-checks all albums next time
            _config.FlickredAlbums = new Dictionary<string, string>(_config.FlickredAlbums);
        }

        return result;
    }

    /// <summary>
    /// Build Immich album description with embedded flickr_id.
    /// Preserves existing user description content.
    /// </summary>
    private static string MakeImmichDescription(string flickrPhotosetId, string existingDescription)
    {
        var flickrRef = $"{{\"flickr_id\":\"{flickrPhotosetId}\"}}";

        // Remove any previous flickr_id from description
        var cleaned = FlickrIdRegex().Replace(existingDescription, "").Trim();

        if (string.IsNullOrEmpty(cleaned))
            return flickrRef;

        return flickrRef + "\n" + cleaned;
    }

    /// <summary>
    /// Build Flickr photoset description with embedded immich_id.
    /// </summary>
    private static string MakeFlickrDescription(string immichAlbumId)
    {
        return $"{{\"immich_id\":\"{immichAlbumId}\"}}";
    }
}
