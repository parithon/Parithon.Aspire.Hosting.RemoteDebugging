using Sample.WorkerApp;

var builder = Host.CreateApplicationBuilder(args);

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

builder.AddServiceDefaults();
builder.Services.AddHttpClient();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
