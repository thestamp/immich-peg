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

app.MapGet("/api/dashboard", async (SyncConfigService cfg, [FromServices] SyncEngine? engine) =>
{
    var config = cfg.Load();
    if (!config.SetupComplete) return Results.Ok(new { setupComplete = false });
    if (!config.DashboardEnabled) return Results.Json(new { error = "Dashboard disabled" }, statusCode: 403);

    var inst = HttpContextHelper.GetOrCreateEngine(cfg, ref engine, app.Services);
    var data = await inst.GetDashboardAsync();
    return Results.Ok(data);
});

app.MapPost("/api/sync/publish", async (SyncConfigService cfg, [FromServices] SyncEngine? engine) =>
{
    var config = cfg.Load();
    if (!config.SetupComplete) return Results.BadRequest(new { error = "Not set up" });

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

    var inst = HttpContextHelper.GetOrCreateEngine(cfg, ref engine, app.Services);
    if (inst.IsRunning) inst.Cancel(); await Task.Delay(500);

    var newEngine = HttpContextHelper.CreateEngine(cfg, app.Services);
    HttpContextHelper.StoreEngine(app.Services, newEngine);

    _ = Task.Run(async () => { try { await newEngine.RunAssetsAsync(); } catch { } });
    return Results.Ok(new { status = "started", action = "assets" });
});

app.MapPost("/api/sync/stop", ([FromServices] SyncConfigService cfg) =>
{
    var engine = app.Services.GetService<SyncEngine>();
    if (engine?.IsRunning == true) { engine.Cancel(); return Results.Ok(new { status = "cancelled" }); }
    return Results.Ok(new { status = "nothing_to_cancel" });
});

// Setup
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
    config.Public.Url = req.PublicUrl.TrimEnd('/');
    config.Public.ApiKey = req.PublicApiKey.Trim();
    config.SyncIntervalMinutes = Math.Clamp(req.SyncInterval, 1, 60);
    config.SetupComplete = true;
    config.SettingsEnabled = !req.LockSettings;
    config.DashboardEnabled = !req.DisableDashboard;
    cfg.Save(config);
    return Results.Ok(new { success = true });
});

app.MapPost("/api/settings", async (SyncConfigService cfg, SetupRequest req) =>
{
    var config = cfg.Load();
    if (!config.SettingsEnabled) return Results.Json(new { error = "Settings locked" }, statusCode: 403);

    config.Main.Url = req.MainUrl.TrimEnd('/');
    if (!string.IsNullOrWhiteSpace(req.MainApiKey)) config.Main.ApiKey = req.MainApiKey.Trim();
    config.Public.Url = req.PublicUrl.TrimEnd('/');
    if (!string.IsNullOrWhiteSpace(req.PublicApiKey)) config.Public.ApiKey = req.PublicApiKey.Trim();
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
        dashboardEnabled = config.DashboardEnabled
    });
});

app.Run();

// Helper
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
    string PublicUrl, string PublicApiKey,
    int SyncInterval = 5,
    bool LockSettings = false,
    bool DisableDashboard = false
);
