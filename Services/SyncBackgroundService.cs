namespace ImmichPeg.Services;

public class SyncBackgroundService : BackgroundService
{
    private readonly SyncConfigService _cfg;
    private readonly ILogger<SyncBackgroundService> _log;

    public static bool IsRunning { get; private set; }

    public SyncBackgroundService(SyncConfigService cfg, ILogger<SyncBackgroundService> log)
    {
        _cfg = cfg; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var config = _cfg.Load();
            if (config.SetupComplete)
            {
                try
                {
                    IsRunning = true;
                    var c = _cfg.Load();
                    c.LastSyncStatus = "running";
                    c.LastSync = DateTime.UtcNow.ToString("o");
                    _cfg.Save(c);

                    _log.LogInformation("Running scheduled sync (target: {Target})...", config.SyncTarget);
                    
                    if (config.SyncTarget == "immich" && !string.IsNullOrWhiteSpace(config.Public.Url))
                    {
                        using var main = new ImmichClient(config.Main.Url, config.Main.ApiKey);
                        using var pub = new ImmichClient(config.Public.Url, config.Public.ApiKey);
                        var engine = new SyncEngine(_cfg, main, pub);
                        await engine.RunPublishAsync();
                        await engine.RunAssetsAsync();
                    }
                    else if (config.SyncTarget == "flickr" && config.Flickr.Enabled && !string.IsNullOrEmpty(config.Flickr.AccessToken))
                    {
                        try
                        {
                            var flickr = new FlickrClient(config.Flickr.ApiKey, config.Flickr.ApiSecret,
                                config.Flickr.AccessToken, config.Flickr.AccessTokenSecret);
                            var flickrEngine = new FlickrSyncEngine(_cfg, flickr, config);
                            var stats = await flickrEngine.RunSyncAsync();
                            c = _cfg.Load();
                            c.LastSyncMessage = $"Albums: {stats["albums_synced"]}, Added: {stats["photos_added"]}, Deleted: {stats["photos_deleted"]}, Skipped: {stats["photos_skipped"]}";
                            _cfg.Save(c);
                        }
                        catch (Exception fex)
                        {
                            _log.LogError(fex, "Flickr sync failed");
                        }
                    }

                    var final = _cfg.Load();
                    final.LastSyncStatus = "ok";
                    final.LastSync = DateTime.UtcNow.ToString("o");
                    _cfg.Save(final);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Scheduled sync failed");
                    var fc = _cfg.Load();
                    fc.LastSyncStatus = "error";
                    fc.LastSyncMessage = ex.Message;
                    _cfg.Save(fc);
                }
                finally
                {
                    IsRunning = false;
                }
            }
            await Task.Delay(TimeSpan.FromMinutes(Math.Max(config.SyncIntervalMinutes, 1)), stoppingToken);
        }
    }
}