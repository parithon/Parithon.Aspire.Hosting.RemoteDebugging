using Aspire.Hosting.RemoteDebugging.Sidecar;
using Aspire.Hosting.RemoteDebugging.Sidecar.Application;
using Aspire.Hosting.RemoteDebugging.Sidecar.Grpc;
using Aspire.Hosting.RemoteDebugging.Sidecar.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Serilog;
using Serilog.Events;

// Bootstrap a minimal logger for startup errors before the host is built.
Log.Logger = new LoggerConfiguration()
  .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
  .Enrich.FromLogContext()
  .WriteTo.Console()
  .CreateBootstrapLogger();

// ── Log file archival ─────────────────────────────────────────────────────────
// Always write to the static "sidecar.log". At startup, if a previous log exists,
// rename it to "sidecar-YYYYMMDD.log" (using the file's last-write date) so the
// current session always has a clean, predictably-named file to tail.
// Retain at most 7 archived logs; purge older ones on every startup.
const int MaxArchivedLogs = 7;

var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logDir);
var currentLogPath = Path.Combine(logDir, "sidecar.log");

if (File.Exists(currentLogPath))
{
  var archiveDate = File.GetLastWriteTimeUtc(currentLogPath).ToString("yyyyMMdd");
  var archivePath = Path.Combine(logDir, $"sidecar-{archiveDate}.log");

  // Avoid collisions if multiple sessions started on the same day.
  for (var i = 1; File.Exists(archivePath); i++)
    archivePath = Path.Combine(logDir, $"sidecar-{archiveDate}-{i}.log");

  try { File.Move(currentLogPath, archivePath); }
  catch (Exception ex) { Log.Warning(ex, "Could not archive previous log to {Path}.", archivePath); }
}

// Purge archives beyond the retention limit (oldest first).
var archives = Directory.GetFiles(logDir, "sidecar-*.log")
  .OrderByDescending(static f => f)
  .Skip(MaxArchivedLogs);
foreach (var stale in archives)
  try { File.Delete(stale); }
  catch { /* best-effort */ }

// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SidecarOptions>(
  builder.Configuration.GetSection(SidecarOptions.SectionName));

// File + console logging via Serilog.
// Current log  : <deploy-dir>/sidecar/logs/sidecar.log   (static — always the active session)
// Archived logs: <deploy-dir>/sidecar/logs/sidecar-YYYYMMDD.log  (renamed at next startup)
builder.Host.UseSerilog((ctx, services, config) =>
{
  config
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Grpc", LogEventLevel.Warning)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
      "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
      path: currentLogPath,
      fileSizeLimitBytes: 100 * 1024 * 1024, // 100 MB safety cap
      rollOnFileSizeLimit: false,
      outputTemplate:
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
});

// gRPC (TCP/HTTP2 only — no REST endpoints)
builder.Services.AddGrpc(opts =>
{
  // Expose error details in development for easier debugging; suppress in production.
  opts.EnableDetailedErrors =
    builder.Environment.IsDevelopment();
});

if (builder.Environment.IsDevelopment())
{
  builder.Services.AddGrpcReflection();
}

// Application services
builder.Services.AddSingleton<IProcessManager, ProcessManagerService>();

// Infrastructure services
builder.Services.AddSingleton<LogCachePersistence>();
builder.Services.AddSingleton<ConnectionMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConnectionMonitor>());

// Kestrel: listen on TCP, HTTP/2 only — no TLS (sidecar runs on a trusted internal network).
builder.WebHost.ConfigureKestrel((context, options) =>
{
  var port = context.Configuration.GetValue($"{SidecarOptions.SectionName}:Port", 5055);
  options.ListenAnyIP(port, listenOptions =>
  {
    listenOptions.Protocols = HttpProtocols.Http2;
  });
});

var app = builder.Build();

app.MapGrpcService<SidecarGrpcService>();

if (app.Environment.IsDevelopment())
{
  app.MapGrpcReflectionService();
}

// Minimal health endpoint — useful for smoke tests before gRPC client is available.
app.MapGet("/healthz", () => Results.Ok("sidecar-healthy"));

app.Run();
