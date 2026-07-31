namespace ImmichPeg.Models;

public class InstanceConfig
{
    public string Url { get; set; } = "";
    public string ApiKey { get; set; } = "";
}

public class SyncConfig
{
    public InstanceConfig Main { get; set; } = new();
    public InstanceConfig Public { get; set; } = new();
    public FlickrConfig Flickr { get; set; } = new();
    public string SyncTarget { get; set; } = "immich";
    public int SyncIntervalMinutes { get; set; } = 5;
    public bool SetupComplete { get; set; }
    public bool SettingsEnabled { get; set; } = true;
    public bool DashboardEnabled { get; set; } = true;
    public string? LastSync { get; set; }
    public string LastSyncStatus { get; set; } = "never";
    public string LastSyncMessage { get; set; } = "";
    public int AlbumsSynced { get; set; }
    public int AssetsCopied { get; set; }
    public int AlbumsRemoved { get; set; }
    public int TotalSharedAlbums { get; set; }
    public int TotalSyncedAlbums { get; set; }
    public Dictionary<string, SyncedAlbum> SyncedAlbums { get; set; } = new();
    public List<RecentAsset> RecentAssets { get; set; } = new();
    public int FlickrAlbumsSynced { get; set; }
    public int FlickrPhotosUploaded { get; set; }
    public int FlickrPhotosDeleted { get; set; }
    public Dictionary<string, string> FlickredAlbums { get; set; } = new();
}

public class SyncedAlbum
{
    public string PublicAlbumId { get; set; } = "";
    public string AlbumName { get; set; } = "";
    public int AssetCount { get; set; }
    public int TotalAssets { get; set; }
    public string? LastSynced { get; set; }
}

public class RecentAsset
{
    public string Filename { get; set; } = "";
    public string AlbumName { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public string Action { get; set; } = "added";    // "added", "deleted", "created", "removed"
    public string Entity { get; set; } = "photo";     // "photo" or "album"
}

public class DashboardData
{
    public Dictionary<string, bool> Health { get; set; } = new();
    public bool SetupComplete { get; set; }
    public int SyncIntervalMinutes { get; set; }
    public string? LastSync { get; set; }
    public string LastSyncStatus { get; set; } = "never";
    public string LastSyncMessage { get; set; } = "";
    public bool SyncActive { get; set; }
    public bool SettingsEnabled { get; set; } = true;
    public bool DashboardEnabled { get; set; } = true;
    public int TotalSharedAlbums { get; set; }
    public int TotalSyncedAlbums { get; set; }
    public int TotalAlbumsSyncedEver { get; set; }
    public int TotalAssetsCopiedEver { get; set; }
    public int TotalAssetsDeletedEver { get; set; }
    public int TotalAlbumsRemovedEver { get; set; }
    public List<SyncedAlbum> SyncedAlbums { get; set; } = new();
    public List<AlbumSyncStatus> AlbumStatuses { get; set; } = new();
    public List<RecentAsset> RecentAssets { get; set; } = new();
    public Dictionary<string, PermissionStatus>? Permissions { get; set; }
    public FlickrStatus? Flickr { get; set; }
    public bool HasPublicDest { get; set; }
    public string SyncTarget { get; set; } = "immich";
}

public class AlbumSyncStatus
{
    public string AlbumId { get; set; } = "";
    public string AlbumName { get; set; } = "";
    public int AssetCount { get; set; }
    public int SyncedCount { get; set; }
    public string? FlickrAlbumId { get; set; }
    public bool Synced => SyncedCount >= AssetCount && FlickrAlbumId != null;
}

public class PermissionStatus
{
    public List<string> Granted { get; set; } = new();
    public List<string> Required { get; set; } = new();
    public List<string> Missing { get; set; } = new();
    public bool Ok => Missing.Count == 0;
}

public class FlickrConfig
{
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string AccessTokenSecret { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Username { get; set; } = "";
    public string RequestTokenSecret { get; set; } = "";
    public bool Enabled { get; set; }
}

public class FlickrStatus
{
    public bool Configured { get; set; }
    public bool Authorized { get; set; }
    public string? Username { get; set; }
    public bool Enabled { get; set; }
    public int AlbumsSynced { get; set; }
    public int PhotosUploaded { get; set; }
}
