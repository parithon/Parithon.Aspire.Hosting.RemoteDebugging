using Sample.WorkerApp;

var builder = Host.CreateApplicationBuilder(args);

var serviceMode = builder.Configuration["service-mode"];
if (!string.IsNullOrWhiteSpace(serviceMode))
{
    switch (serviceMode.Trim().ToLowerInvariant())
    {
        case "windows":
            builder.Services.AddWindowsService();
            // AddWindowsService() → EventLogSettingsSetup sets EventLogSettings.SourceName to
            // IHostEnvironment.ApplicationName ("Sample.WorkerApp") if the property is empty.
            // EventLogSettings is NOT automatically bound from IConfiguration, so the
            // Logging__EventLog__SourceName env var injected by the Aspire runner has no effect
            // unless we wire it up explicitly here.  Reading the value from IConfiguration and
            // calling AddEventLog() ensures the source name registered in the EventLog registry
            // (and filtered by the Aspire watcher script) matches what the provider actually uses.
            builder.Logging.AddEventLog(settings =>
            {
                var src = builder.Configuration["Logging:EventLog:SourceName"];
                if (!string.IsNullOrEmpty(src))
                    settings.SourceName = src;
            });
            break;
        default:
            throw new InvalidOperationException(
                "Invalid value for 'service-mode'. Supported value is 'windows'.");
    }
}

builder.AddServiceDefaults();
builder.Services.AddHttpClient();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
