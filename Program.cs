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

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// ── Dashboard ───────────────────────────────────────────────────

app.MapGet("/api/dashboard", async (SyncConfigService cfg, [FromServices] SyncEngine? engine) =>
{
    var config = cfg.Load();
    if (!config.SetupComplete) return Results.Ok(new { setupComplete = false });
    if (!config.DashboardEnabled) return Results.Json(new { error = "Dashboard disabled" }, statusCode: 403);

    var inst = HttpContextHelper.GetOrCreateEngine(cfg, ref engine, app.Services);
    var data = await inst.GetDashboardAsync();
    return Results.Ok(data);
});

// ── Sync triggers ───────────────────────────────────────────────

app.MapPost("/api/sync/publish", async (SyncConfigService cfg, [FromServices] SyncEngine? engine) =>
{
    var config = cfg.Load();
    if (!config.SetupComplete) return Results.BadRequest(new { error = "Not set up" });
    if (config.SyncTarget != "immich") return Results.BadRequest(new { error = "Sync target is not public Immich" });
    if (string.IsNullOrWhiteSpace(config.Public.Url)) return Results.BadRequest(new { error = "Public Immich not configured" });

    var inst = HttpContextHelper.GetOrCreateEngine(cfg, ref engine, app.Services);
    if (inst.IsRunning) inst.Cancel(); await Task.Delay(500);

    var newEngine = HttpContextHelper.CreateEngine(cfg, app.Services);
    HttpContextHelper.StoreEngine(app.Services, newEngine);

    _ = Task.Run(async () => { try { await newEngine.RunPublishAsync(); } catch { } });
    return Results.Ok(new { status = "started", action = "publish" });
});

app.MapPost("/api/sync/assets", async (SyncConfigService cfg, [FromServices] SyncEngine? engine) =>
{
    var config = cfg.Load();
    if (!config.SetupComplete) return Results.BadRequest(new { error = "Not set up" });
    if (config.SyncTarget != "immich") return Results.BadRequest(new { error = "Sync target is not public Immich" });
    if (string.IsNullOrWhiteSpace(config.Public.Url)) return Results.BadRequest(new { error = "Public Immich not configured" });

    var inst = HttpContextHelper.GetOrCreateEngine(cfg, ref engine, app.Services);
    if (inst.IsRunning) inst.Cancel(); await Task.Delay(500);

    var newEngine = HttpContextHelper.CreateEngine(cfg, app.Services);
    HttpContextHelper.StoreEngine(app.Services, newEngine);

    _ = Task.Run(async () => { try { await newEngine.RunAssetsAsync(); } catch { } });
    return Results.Ok(new { status = "started", action = "assets" });
});

app.MapPost("/api/sync/flickr", async (SyncConfigService cfg) =>
{
    var config = cfg.Load();
    if (!config.SetupComplete) return Results.BadRequest(new { error = "Not set up" });
    if (config.SyncTarget != "flickr") return Results.BadRequest(new { error = "Sync target is not Flickr" });
    if (!config.Flickr.Enabled) return Results.BadRequest(new { error = "Flickr not configured" });

    var flickr = new FlickrClient(config.Flickr.ApiKey, config.Flickr.ApiSecret,
        config.Flickr.AccessToken, config.Flickr.AccessTokenSecret);
    var engine = new FlickrSyncEngine(cfg, flickr, config);

    _ = Task.Run(async () => { try { await engine.RunSyncAsync(); } catch { } });
    return Results.Ok(new { status = "started", action = "flickr" });
});

app.MapPost("/api/sync/stop", ([FromServices] SyncConfigService cfg) =>
{
    var engine = app.Services.GetService<SyncEngine>();
    if (engine?.IsRunning == true) { engine.Cancel(); return Results.Ok(new { status = "cancelled" }); }
    return Results.Ok(new { status = "nothing_to_cancel" });
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
        config.Flickr = new FlickrConfig(); // clear Flickr when choosing Immich
    }
    else // flickr
    {
        if (string.IsNullOrWhiteSpace(req.FlickrApiKey) || string.IsNullOrWhiteSpace(req.FlickrApiSecret))
            return Results.BadRequest(new { error = "Flickr API key and secret are required" });
        config.Flickr.ApiKey = req.FlickrApiKey.Trim();
        config.Flickr.ApiSecret = req.FlickrApiSecret.Trim();
        // OAuth must already be completed before setup can finish
        if (string.IsNullOrWhiteSpace(config.Flickr.AccessToken))
            return Results.BadRequest(new { error = "Complete Flickr authorization before finishing setup" });
        config.Flickr.Enabled = true;
        config.Public = new InstanceConfig(); // clear public Immich when choosing Flickr
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
        if (!string.IsNullOrWhiteSpace(config.Public.Url) && string.IsNullOrWhiteSpace(config.Public.ApiKey))
            return Results.BadRequest(new { error = "Public Immich API key is required when URL is set" });
        config.Flickr = new FlickrConfig(); // clear Flickr when switching to Immich
    }
    else // flickr
    {
        if (!string.IsNullOrWhiteSpace(req.FlickrApiKey)) config.Flickr.ApiKey = req.FlickrApiKey.Trim();
        if (!string.IsNullOrWhiteSpace(req.FlickrApiSecret)) config.Flickr.ApiSecret = req.FlickrApiSecret.Trim();
        if (string.IsNullOrWhiteSpace(config.Flickr.ApiKey) || string.IsNullOrWhiteSpace(config.Flickr.ApiSecret))
            return Results.BadRequest(new { error = "Flickr API key and secret are required" });
        if (string.IsNullOrWhiteSpace(config.Flickr.AccessToken))
            return Results.BadRequest(new { error = "Complete Flickr authorization before saving settings" });
        config.Flickr.Enabled = true;
        config.Public = new InstanceConfig(); // clear public Immich when switching to Flickr
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
    var url = client.GetAuthorizationUrl(callback);
    return Results.Ok(new { url });
});

app.MapGet("/api/flickr/callback", (SyncConfigService cfg, string oauth_token, string oauth_verifier) =>
{
    var config = cfg.Load();
    var client = new FlickrClient(config.Flickr.ApiKey, config.Flickr.ApiSecret);
    try
    {
        var (token, tokenSecret, userId, username) = client.CompleteAuthorization(oauth_token, oauth_verifier);
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

// ── Helpers ──────────────────────────────────────────────────────

static class HttpContextHelper
{
    private static SyncEngine? _engine;

    public static SyncEngine GetOrCreateEngine(SyncConfigService cfg, ref SyncEngine? engine, IServiceProvider sp)
    {
        engine ??= CreateEngine(cfg, sp);
        return engine;
    }

    public static SyncEngine CreateEngine(SyncConfigService cfg, IServiceProvider sp)
    {
        var config = cfg.Load();
        var main = new ImmichClient(config.Main.Url, config.Main.ApiKey);
        var pub = new ImmichClient(config.Public.Url, config.Public.ApiKey);
        return new SyncEngine(cfg, main, pub);
    }

    public static void StoreEngine(IServiceProvider sp, SyncEngine engine) => _engine = engine;
}

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
