using System.CommandLine;
using Aspire.Hosting.RemoteDebugging.Sidecar.Grpc;
using Aspire.Hosting.RemoteDebugging.Sidecar.Proto;
using Grpc.Core;
using Grpc.Net.Client;

// ── Constants ────────────────────────────────────────────────────────────────
const int SidecarPort = 5055;

var rootCommand = new RootCommand("aspire-sidecar — remote process supervisor for Aspire remote debugging");

// ── daemon ───────────────────────────────────────────────────────────────────
var daemonCommand = new Command("daemon", "Start the sidecar gRPC daemon (invoked once per remote host on connect)");
daemonCommand.SetAction(async (ParseResult _, CancellationToken ct) =>
{
  var builder = WebApplication.CreateSlimBuilder();

  builder.WebHost.ConfigureKestrel(kestrel =>
  {
    // Listen on localhost only — the AppHost connects via SSH port forwarding.
    kestrel.ListenLocalhost(SidecarPort, opts => opts.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
  });

  builder.Services.AddGrpc();
  builder.Services.AddSingleton<SidecarGrpcService>();

  var app = builder.Build();
  app.MapGrpcService<SidecarGrpcService>();

  var service = app.Services.GetRequiredService<SidecarGrpcService>();
  await using var _svc = service;

  await app.RunAsync(ct);
});

// ── Helper: connect to the local daemon ─────────────────────────────────────
static GrpcChannel CreateChannel() =>
  GrpcChannel.ForAddress($"http://localhost:{SidecarPort}");

// ── start ────────────────────────────────────────────────────────────────────
var startProjectOpt = new Option<string>("--project") { Description = "Project name", Required = true };
var startPathOpt    = new Option<string>("--path")    { Description = "Remote deployment directory", Required = true };
var startExeOpt     = new Option<string>("--executable") { Description = "Executable name (no extension)", Required = true };

var startCommand = new Command("start", "Start a project process on the remote host");
startCommand.Add(startProjectOpt);
startCommand.Add(startPathOpt);
startCommand.Add(startExeOpt);
startCommand.SetAction(async (ParseResult result, CancellationToken ct) =>
{
  using var channel = CreateChannel();
  var client = new SidecarService.SidecarServiceClient(channel);
  var resp   = await client.StartAsync(new StartRequest
  {
    Project    = result.GetValue(startProjectOpt)!,
    Path       = result.GetValue(startPathOpt)!,
    Executable = result.GetValue(startExeOpt)!,
  }, cancellationToken: ct);
  Console.WriteLine($"pid={resp.Pid}");
});

// ── stop ─────────────────────────────────────────────────────────────────────
var stopProjectOpt = new Option<string>("--project") { Description = "Project name", Required = true };

var stopCommand = new Command("stop", "Stop a running project process");
stopCommand.Add(stopProjectOpt);
stopCommand.SetAction(async (ParseResult result, CancellationToken ct) =>
{
  using var channel = CreateChannel();
  var client = new SidecarService.SidecarServiceClient(channel);
  await client.StopAsync(new StopRequest { Project = result.GetValue(stopProjectOpt)! }, cancellationToken: ct);
  Console.WriteLine("stopped");
});

// ── status ───────────────────────────────────────────────────────────────────
var statusProjectOpt = new Option<string>("--project") { Description = "Project name", Required = true };

var statusCommand = new Command("status", "Report whether a project process is running");
statusCommand.Add(statusProjectOpt);
statusCommand.SetAction(async (ParseResult result, CancellationToken ct) =>
{
  using var channel = CreateChannel();
  var client = new SidecarService.SidecarServiceClient(channel);
  var resp   = await client.GetStatusAsync(new StatusRequest { Project = result.GetValue(statusProjectOpt)! }, cancellationToken: ct);
  Console.WriteLine(resp.Running ? $"running pid={resp.Pid}" : "exited");
});

// ── logs ─────────────────────────────────────────────────────────────────────
var logsProjectOpt = new Option<string>("--project") { Description = "Project name", Required = true };
var logsFromOpt    = new Option<long>("--from")
{
  Description = "Byte offset to resume streaming from",
  DefaultValueFactory = _ => 0L,
};

var logsCommand = new Command("logs", "Stream stdout/stderr from a project process (long-running)");
logsCommand.Add(logsProjectOpt);
logsCommand.Add(logsFromOpt);
logsCommand.SetAction(async (ParseResult result, CancellationToken ct) =>
{
  using var channel = CreateChannel();
  var client = new SidecarService.SidecarServiceClient(channel);
  using var stream = client.StreamLogs(new StreamLogsRequest
  {
    Project    = result.GetValue(logsProjectOpt)!,
    FromOffset = result.GetValue(logsFromOpt),
  }, cancellationToken: ct);

  await foreach (var line in stream.ResponseStream.ReadAllAsync(ct))
    Console.WriteLine(line.Line);
});

// ── wire up ──────────────────────────────────────────────────────────────────
rootCommand.Add(daemonCommand);
rootCommand.Add(startCommand);
rootCommand.Add(stopCommand);
rootCommand.Add(statusCommand);
rootCommand.Add(logsCommand);

return await rootCommand.Parse(args).InvokeAsync(new InvocationConfiguration());

