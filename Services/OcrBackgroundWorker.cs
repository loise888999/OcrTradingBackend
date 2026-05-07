using Microsoft.Extensions.Options;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public sealed class OcrBackgroundWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OcrControlState _control;
    private readonly IOptionsMonitor<OcrRuntimeSettings> _settings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OcrBackgroundWorker> _logger;

    public OcrBackgroundWorker(
        IServiceScopeFactory scopeFactory,
        OcrControlState control,
        IOptionsMonitor<OcrRuntimeSettings> settings,
        IConfiguration configuration,
        ILogger<OcrBackgroundWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _control = control;
        _settings = settings;
        _configuration = configuration;
        _logger = logger;
        _control.Enabled = settings.CurrentValue.Enabled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = _settings.CurrentValue;

            try
            {
                if (_control.Enabled)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var runner = scope.ServiceProvider.GetRequiredService<IOcrCycleRunner>();

                    await runner.RunOneCycleAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _control.LastError = ex.Message;
                _logger.LogError(ex, "OCR background cycle failed");
            }

            await Task.Delay(
                GetLoopDelay(settings),
                stoppingToken);
        }
    }

    private TimeSpan GetLoopDelay(OcrRuntimeSettings settings)
    {
        // This lets price batch capture run faster than the old 1-second minimum.
        // If this setting is missing, the app keeps the old DefaultIntervalSeconds behavior.
        var configuredMilliseconds = _configuration.GetValue<int?>(
            "OcrSettings:DefaultIntervalMilliseconds");

        if (configuredMilliseconds is not null)
            return TimeSpan.FromMilliseconds(Math.Clamp(configuredMilliseconds.Value, 25, 60_000));

        return TimeSpan.FromSeconds(Math.Max(1, settings.DefaultIntervalSeconds));
    }
}
