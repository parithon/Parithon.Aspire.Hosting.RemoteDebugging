using Aspire.Hosting.RemoteDebugging.Sidecar;
using Aspire.Hosting.RemoteDebugging.Sidecar.Application;
using Aspire.Hosting.RemoteDebugging.Sidecar.Grpc;
using Aspire.Hosting.RemoteDebugging.Sidecar.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SidecarOptions>(
  builder.Configuration.GetSection(SidecarOptions.SectionName));

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
