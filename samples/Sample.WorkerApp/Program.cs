using Sample.WorkerApp;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

// Configure service defaults (OpenTelemetry, service discovery, health checks) FIRST
// This sets up the logging pipeline with OpenTelemetry export to the apphost
builder.AddServiceDefaults();

// Resolve log file path and output template — injected by the AppHost
// or falling back to a sensible default for standalone runs.
var logFilePath = builder.Configuration["Logging:FilePath"]
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "Logs", "remote-worker", "worker.log");

var outputTemplate = builder.Configuration["Logging:OutputTemplate"]
    ?? "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);

// Add Serilog as an ADDITIONAL logging sink (not a replacement)
// This preserves the OpenTelemetry logging configured by AddServiceDefaults()
builder.Logging.AddSerilog(new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File(
        path: logFilePath,
        rollingInterval: RollingInterval.Infinite,
        retainedFileCountLimit: 1,
        outputTemplate: outputTemplate)
    .CreateLogger());

var serviceMode = builder.Configuration["service-mode"];
if (!string.IsNullOrWhiteSpace(serviceMode))
{
    switch (serviceMode.Trim().ToLowerInvariant())
    {
        case "windows":
            builder.Services.AddWindowsService();
            break;
        default:
            throw new InvalidOperationException(
                "Invalid value for 'service-mode'. Supported value is 'windows'.");
    }
}

builder.Services.AddHttpClient();
builder.Services.AddHostedService<Worker>();

try
{
    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
