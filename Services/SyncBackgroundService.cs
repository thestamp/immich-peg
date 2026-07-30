namespace ImmichPeg.Services;

public class SyncBackgroundService : BackgroundService
{
    private readonly SyncConfigService _cfg;
    private readonly ILogger<SyncBackgroundService> _log;

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
                            await flickrEngine.RunSyncAsync();
                        }
                        catch (Exception fex)
                        {
                            _log.LogError(fex, "Flickr sync failed");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Scheduled sync failed");
                }
            }
            await Task.Delay(TimeSpan.FromMinutes(Math.Max(config.SyncIntervalMinutes, 1)), stoppingToken);
        }
    }
}
