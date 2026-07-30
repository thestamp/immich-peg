using ImmichPeg.Models;

namespace ImmichPeg.Services;

public class FlickrSyncEngine
{
    private readonly SyncConfigService _configService;
    private readonly FlickrClient _flickr;
    private readonly ImmichClient _immich;
    private readonly SyncConfig _config;

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
            ["albums_synced"] = 0, ["photos_uploaded"] = 0, ["errors"] = new List<string>()
        };

        try
        {
            var sharedAlbums = await _immich.GetAllAlbumsAsync(true);

            foreach (var album in sharedAlbums)
            {
                try
                {
                    var albumId = album.GetProperty("id").GetString()!;
                    var albumName = album.GetProperty("albumName").GetString()!;
                    var result = await SyncAlbumToFlickrAsync(albumId, albumName);
                    stats["albums_synced"] = (int)stats["albums_synced"] + 1;
                    stats["photos_uploaded"] = (int)stats["photos_uploaded"] + (int)result["uploaded"];
                }
                catch (Exception ex)
                {
                    ((List<string>)stats["errors"]).Add($"Error: {ex.Message}");
                }
            }
        }
        finally
        {
            _configService.Save(_config);
        }

        return stats;
    }

    private async Task<Dictionary<string, object>> SyncAlbumToFlickrAsync(string albumId, string albumName)
    {
        var result = new Dictionary<string, object> { ["uploaded"] = 0 };

        // Get or create photoset
        string? photosetId;
        if (_config.FlickredAlbums.TryGetValue(albumId, out var existingId))
        {
            photosetId = existingId;
            // Verify it still exists
            try { await _flickr.GetPhotosetPhotoIdsAsync(photosetId); }
            catch { photosetId = null; }
        }
        else
        {
            photosetId = null;
        }

        // Get main Immich assets for this album
        var assets = await _immich.GetAlbumAssetsAsync(albumId);

        // Get existing photo IDs in this photoset
        var existingPhotoIds = photosetId != null
            ? await _flickr.GetPhotosetPhotoIdsAsync(photosetId)
            : new HashSet<string>();

        // Upload new photos
        foreach (var asset in assets)
        {
            var originalName = asset.TryGetProperty("originalFileName", out var on) ? on.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(originalName)) continue;

            try
            {
                var assetId = asset.GetProperty("id").GetString()!;
                var assetData = await _immich.DownloadAssetAsync(assetId);

                var title = Path.GetFileNameWithoutExtension(originalName);
                var photoId = await _flickr.UploadPhotoAsync(assetData, originalName, title,
                    $"From Immich album: {albumName}");

                result["uploaded"] = (int)result["uploaded"] + 1;
                _config.FlickrPhotosUploaded++;

                // Create photoset with first photo if needed
                if (photosetId == null)
                {
                    photosetId = await _flickr.CreatePhotosetAsync(albumName, photoId);
                    _config.FlickredAlbums[albumId] = photosetId;
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
                Console.Error.WriteLine($"Flickr upload failed: {originalName}: {ex.Message}");
            }
        }

        if (photosetId != null)
        {
            _config.FlickredAlbums[albumId] = photosetId;
            _config.FlickrAlbumsSynced = _config.FlickredAlbums.Count;
        }

        return result;
    }
}
