using System.Net.Http.Json;

namespace Sample.WorkerApp;

public class Worker(IHttpClientFactory clientFactory, ILogger<Worker> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        using var client = clientFactory.CreateClient();
        var ipifyresults = await client.GetFromJsonAsync<IpifyResponse>("https://api64.ipify.org?format=json", cancellationToken);
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("IP: {IPAddress}", ipifyresults?.ip);
        }
        await base.StartAsync(cancellationToken);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
            }
            await Task.Delay(1000, stoppingToken);
        }
    }
}

public record IpifyResponse(string ip);