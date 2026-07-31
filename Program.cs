using ImmichPeg.Models;
using ImmichPeg.Services;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<SyncConfigService>();
builder.Services.AddHostedService<SyncBackgroundService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

static string MaskKey(string key) =>
    string.IsNullOrEmpty(key) ? "" :
    key.Length <= 8 ? new string('\u2022', key.Length) :
    key[..4] + new string('\u2022', Math.Min(8, key.Length - 8)) + key[^4..];

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

// ── Dashboard ───────────────────────────────────────────────────

app.MapGet("/api/dashboard", async (SyncConfigService cfg) =>
{
    var config = cfg.Load();
    if (!config.SetupComplete) return Results.Ok(new { setupComplete = false });
    if (!config.DashboardEnabled) return Results.Json(new { error = "Dashboard disabled" }, statusCode: 403);

    var health = new Dictionary<string, bool>();
    try
    {
        using var immich = new ImmichClient(config.Main.Url, config.Main.ApiKey);
        health["main"] = await immich.PingAsync();
    }
    catch { health["main"] = false; }

    // Build album statuses
    var albumStatuses = new List<AlbumSyncStatus>();
    if (config.SyncTarget == "flickr" && !string.IsNullOrEmpty(config.Flickr.AccessToken))
    {
        try
        {
            using var immich = new ImmichClient(config.Main.Url, config.Main.ApiKey);
            var sharedAlbums = await immich.GetAllAlbumsAsync(true);
            config.TotalSharedAlbums = sharedAlbums.Count;

            foreach (var album in sharedAlbums)
            {
                var albumId = album.GetProperty("id").GetString()!;
                var albumName = album.GetProperty("albumName").GetString()!;
                var assetCount = album.GetProperty("assetCount").GetInt32();
                var syncedCount = 0;
                var flickrAlbumId = config.FlickredAlbums.TryGetValue(albumId, out var fid) ? fid : null;

                albumStatuses.Add(new AlbumSyncStatus
                {
                    AlbumId = albumId,
                    AlbumName = albumName,
                    AssetCount = assetCount,
                    SyncedCount = syncedCount,
                    FlickrAlbumId = flickrAlbumId
                });
            }

            config.TotalSyncedAlbums = config.FlickredAlbums.Count;
            cfg.Save(config);
        }
        catch { }
    }

    return Results.Ok(new DashboardData
    {
        SetupComplete = config.SetupComplete,
        SyncIntervalMinutes = config.SyncIntervalMinutes,
        LastSync = config.LastSync,
        LastSyncStatus = config.LastSyncStatus,
        LastSyncMessage = config.LastSyncMessage,
        SyncActive = SyncBackgroundService.IsRunning,
        SettingsEnabled = config.SettingsEnabled,
        DashboardEnabled = config.DashboardEnabled,
        TotalSharedAlbums = config.TotalSharedAlbums,
        TotalSyncedAlbums = config.FlickredAlbums.Count,
        TotalAssetsCopiedEver = config.FlickrPhotosUploaded,
        TotalAssetsDeletedEver = config.FlickrPhotosDeleted,
        TotalAlbumsRemovedEver = config.AlbumsRemoved,
        SyncedAlbums = config.SyncedAlbums.Values.ToList(),
        AlbumStatuses = albumStatuses,
        RecentAssets = config.RecentAssets.Take(20).ToList(),
        Health = health,
        SyncTarget = config.SyncTarget,
        HasPublicDest = !string.IsNullOrWhiteSpace(config.Public.Url),
        Flickr = new FlickrStatus
        {
            Configured = !string.IsNullOrEmpty(config.Flickr.ApiKey),
            Authorized = !string.IsNullOrEmpty(config.Flickr.AccessToken),
            Username = config.Flickr.Username,
            Enabled = config.Flickr.Enabled,
            AlbumsSynced = config.FlickrAlbumsSynced,
            PhotosUploaded = config.FlickrPhotosUploaded
        }
    });
});

// ── Sync triggers ───────────────────────────────────────────────

app.MapPost("/api/sync/flickr", async (SyncConfigService cfg) =>
{
    var config = cfg.Load();
    if (!config.SetupComplete) return Results.BadRequest(new { error = "Not set up" });
    if (config.SyncTarget != "flickr") return Results.BadRequest(new { error = "Sync target is not Flickr" });
    if (!config.Flickr.Enabled) return Results.BadRequest(new { error = "Flickr not configured" });

    var flickr = new FlickrClient(config.Flickr.ApiKey, config.Flickr.ApiSecret,
        config.Flickr.AccessToken, config.Flickr.AccessTokenSecret);
    var engine = new FlickrSyncEngine(cfg, flickr, config);

    _ = Task.Run(async () => {
        try
        {
            SyncBackgroundService.IsRunning = true;
            var c = cfg.Load();
            c.LastSyncStatus = "running";
            c.LastSync = DateTime.UtcNow.ToString("o");
            cfg.Save(c);

            var stats = await engine.RunSyncAsync();

            c = cfg.Load();
            c.LastSyncStatus = "ok";
            c.LastSync = DateTime.UtcNow.ToString("o");
            c.LastSyncMessage = $"Albums: {stats["albums_synced"]}, Added: {stats["photos_added"]}, Deleted: {stats["photos_deleted"]}, Skipped: {stats["photos_skipped"]}";
            cfg.Save(c);
        }
        catch (Exception ex)
        {
            var c = cfg.Load();
            c.LastSyncStatus = "error";
            c.LastSyncMessage = ex.Message;
            cfg.Save(c);
        }
        finally
        {
            SyncBackgroundService.IsRunning = false;
        }
    });
    return Results.Ok(new { status = "started", action = "flickr" });
});

// ── Setup ───────────────────────────────────────────────────────

app.MapGet("/api/setup/status", ([FromServices] SyncConfigService cfg) =>
{
    var c = cfg.Load();
    return Results.Ok(new { setupComplete = c.SetupComplete });
});

app.MapPost("/api/setup", async (SyncConfigService cfg, SetupRequest req) =>
{
    var config = cfg.Load();
    config.Main.Url = req.MainUrl.TrimEnd('/');
    config.Main.ApiKey = req.MainApiKey.Trim();
    config.SyncIntervalMinutes = Math.Clamp(req.SyncInterval, 1, 60);

    var target = (req.SyncTarget ?? "immich").ToLower();
    if (target != "immich" && target != "flickr")
        return Results.BadRequest(new { error = "Sync target must be 'immich' or 'flickr'" });

    if (target == "immich")
    {
        if (string.IsNullOrWhiteSpace(req.PublicUrl))
            return Results.BadRequest(new { error = "Public Immich URL is required" });
        if (string.IsNullOrWhiteSpace(req.PublicApiKey))
            return Results.BadRequest(new { error = "Public Immich API key is required" });
        config.Public.Url = req.PublicUrl.TrimEnd('/');
        config.Public.ApiKey = req.PublicApiKey.Trim();
        config.Flickr = new FlickrConfig();
    }
    else
    {
        if (string.IsNullOrWhiteSpace(req.FlickrApiKey) || string.IsNullOrWhiteSpace(req.FlickrApiSecret))
            return Results.BadRequest(new { error = "Flickr API key and secret are required" });
        config.Flickr.ApiKey = req.FlickrApiKey.Trim();
        config.Flickr.ApiSecret = req.FlickrApiSecret.Trim();
        if (string.IsNullOrWhiteSpace(config.Flickr.AccessToken))
            return Results.BadRequest(new { error = "Complete Flickr authorization before finishing setup" });
        config.Flickr.Enabled = true;
        config.Public = new InstanceConfig();
    }

    config.SyncTarget = target;
    config.SetupComplete = true;
    config.SettingsEnabled = !req.LockSettings;
    config.DashboardEnabled = !req.DisableDashboard;
    cfg.Save(config);
    return Results.Ok(new { success = true });
});

// ── Settings ────────────────────────────────────────────────────

app.MapPost("/api/settings", async (SyncConfigService cfg, SetupRequest req) =>
{
    var config = cfg.Load();
    if (!config.SettingsEnabled) return Results.Json(new { error = "Settings locked" }, statusCode: 403);

    config.Main.Url = req.MainUrl.TrimEnd('/');
    if (!string.IsNullOrWhiteSpace(req.MainApiKey)) config.Main.ApiKey = req.MainApiKey.Trim();

    var target = (req.SyncTarget ?? config.SyncTarget).ToLower();
    if (target != "immich" && target != "flickr")
        return Results.BadRequest(new { error = "Sync target must be 'immich' or 'flickr'" });

    if (target == "immich")
    {
        config.Public.Url = req.PublicUrl?.TrimEnd('/') ?? config.Public.Url;
        if (!string.IsNullOrWhiteSpace(req.PublicApiKey)) config.Public.ApiKey = req.PublicApiKey.Trim();
        config.Flickr = new FlickrConfig();
    }
    else
    {
        if (!string.IsNullOrWhiteSpace(req.FlickrApiKey) && !req.FlickrApiKey.Contains("\u2022")) config.Flickr.ApiKey = req.FlickrApiKey.Trim();
        if (!string.IsNullOrWhiteSpace(req.FlickrApiSecret) && !req.FlickrApiSecret.Contains("\u2022")) config.Flickr.ApiSecret = req.FlickrApiSecret.Trim();
        if (string.IsNullOrWhiteSpace(config.Flickr.AccessToken))
            return Results.BadRequest(new { error = "Complete Flickr authorization before saving settings" });
        config.Flickr.Enabled = true;
        config.Public = new InstanceConfig();
    }

    config.SyncTarget = target;
    config.SyncIntervalMinutes = Math.Clamp(req.SyncInterval, 1, 60);
    if (req.LockSettings) config.SettingsEnabled = false;
    if (req.DisableDashboard) config.DashboardEnabled = false;
    cfg.Save(config);
    return Results.Ok(new { success = true });
});

app.MapGet("/api/settings", (SyncConfigService cfg) =>
{
    var config = cfg.Load();
    if (!config.SetupComplete) return Results.BadRequest(new { error = "Not set up" });
    if (!config.SettingsEnabled) return Results.Json(new { error = "Settings locked" }, statusCode: 403);
    return Results.Ok(new
    {
        mainUrl = config.Main.Url,
        publicUrl = config.Public.Url,
        mainApiKey = MaskKey(config.Main.ApiKey),
        publicApiKey = MaskKey(config.Public.ApiKey),
        syncIntervalMinutes = config.SyncIntervalMinutes,
        settingsEnabled = config.SettingsEnabled,
        dashboardEnabled = config.DashboardEnabled,
        flickrApiKey = MaskKey(config.Flickr.ApiKey),
        flickrApiSecret = string.IsNullOrEmpty(config.Flickr.ApiSecret) ? "" : "\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022",
        flickrAccessToken = string.IsNullOrEmpty(config.Flickr.AccessToken) ? "" : "\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022",
        flickrEnabled = config.Flickr.Enabled,
        flickrAuthorized = !string.IsNullOrEmpty(config.Flickr.AccessToken),
        flickrUsername = config.Flickr.Username,
        publicConfigured = !string.IsNullOrWhiteSpace(config.Public.Url),
        syncTarget = config.SyncTarget
    });
});

// ── Flickr OAuth flow ───────────────────────────────────────────

app.MapPost("/api/flickr/authorize", (SyncConfigService cfg, HttpContext http) =>
{
    var config = cfg.Load();
    if (string.IsNullOrEmpty(config.Flickr.ApiKey) || string.IsNullOrEmpty(config.Flickr.ApiSecret))
        return Results.BadRequest(new { error = "Flickr API key not configured" });

    var client = new FlickrClient(config.Flickr.ApiKey, config.Flickr.ApiSecret);
    var callback = $"{http.Request.Scheme}://{http.Request.Host}/api/flickr/callback";
    var (url, requestTokenSecret) = client.GetAuthorizationUrl(callback);
    config.Flickr.RequestTokenSecret = requestTokenSecret;
    cfg.Save(config);
    return Results.Ok(new { url });
});

app.MapGet("/api/flickr/callback", (SyncConfigService cfg, string oauth_token, string oauth_verifier) =>
{
    var config = cfg.Load();
    var client = new FlickrClient(config.Flickr.ApiKey, config.Flickr.ApiSecret);
    try
    {
        var (token, tokenSecret, userId, username) = client.CompleteAuthorization(
            oauth_token, oauth_verifier, config.Flickr.RequestTokenSecret);
        config.Flickr.AccessToken = token;
        config.Flickr.AccessTokenSecret = tokenSecret;
        config.Flickr.UserId = userId;
        config.Flickr.Username = username;
        config.Flickr.Enabled = true;
        cfg.Save(config);
        return Results.Content(
            "<html><body style='font-family:sans-serif;text-align:center;padding-top:80px'>" +
            "<h2>Flickr authorized!</h2><p>Connected as <strong>" + username + "</strong></p>" +
            "<p>You can close this window.</p></body></html>",
            "text/html");
    }
    catch (Exception ex)
    {
        return Results.Content(
            "<html><body style='font-family:sans-serif;text-align:center;padding-top:80px'>" +
            "<h2>Authorization failed</h2><p>" + ex.Message + "</p></body></html>",
            "text/html");
    }
});

app.MapGet("/api/flickr/status", (SyncConfigService cfg) =>
{
    var config = cfg.Load();
    return Results.Ok(new FlickrStatus
    {
        Configured = !string.IsNullOrEmpty(config.Flickr.ApiKey),
        Authorized = !string.IsNullOrEmpty(config.Flickr.AccessToken),
        Username = config.Flickr.Username,
        Enabled = config.Flickr.Enabled,
        AlbumsSynced = config.FlickrAlbumsSynced,
        PhotosUploaded = config.FlickrPhotosUploaded
    });
});

app.MapPost("/api/flickr/disconnect", (SyncConfigService cfg) =>
{
    var config = cfg.Load();
    config.Flickr.AccessToken = "";
    config.Flickr.AccessTokenSecret = "";
    config.Flickr.UserId = "";
    config.Flickr.Username = "";
    config.Flickr.Enabled = false;
    cfg.Save(config);
    return Results.Ok(new { success = true });
});

app.Run();

public record SetupRequest(
    string MainUrl, string MainApiKey,
    string? PublicUrl = null, string? PublicApiKey = null,
    int SyncInterval = 5,
    bool LockSettings = false,
    bool DisableDashboard = false,
    string? FlickrApiKey = null,
    string? FlickrApiSecret = null,
    string? FlickrEnabled = null,
    string? SyncTarget = null
);