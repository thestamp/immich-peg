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
                    _log.LogInformation("Running scheduled sync...");
                    using var main = new ImmichClient(config.Main.Url, config.Main.ApiKey);
                    using var pub = new ImmichClient(config.Public.Url, config.Public.ApiKey);
                    var engine = new SyncEngine(_cfg, main, pub);
                    await engine.RunPublishAsync();
                    await engine.RunAssetsAsync();
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
