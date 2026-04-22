using System.CommandLine;
using Aspire.Hosting.RemoteDebugging.Sidecar.Daemon;

var rootCommand = new RootCommand("aspire-sidecar — remote process supervisor for Aspire remote debugging");

// ── daemon ────────────────────────────────────────────────────────────────────
var daemonCommand = new Command("daemon", "Start the sidecar daemon (invoked once per remote host on connect)");
daemonCommand.SetAction(async (ParseResult _, CancellationToken ct) =>
{
  await using var daemon = new SidecarDaemon();
  await daemon.RunAsync(ct);
});

// ── start ─────────────────────────────────────────────────────────────────────
var startProjectOpt = new Option<string>("--project")
{
  Description = "Project name",
  Required = true,
};
var startPathOpt = new Option<string>("--path")
{
  Description = "Remote deployment directory",
  Required = true,
};
var startExecutableOpt = new Option<string>("--executable")
{
  Description = "Executable name (no extension)",
  Required = true,
};

var startCommand = new Command("start", "Start a project process on the remote host");
startCommand.Add(startProjectOpt);
startCommand.Add(startPathOpt);
startCommand.Add(startExecutableOpt);
startCommand.SetAction(async (ParseResult result, CancellationToken ct) =>
{
  var project    = result.GetValue(startProjectOpt)!;
  var path       = result.GetValue(startPathOpt)!;
  var executable = result.GetValue(startExecutableOpt)!;

  var client = new DaemonClient();
  var pid = await client.StartProjectAsync(project, path, executable, ct);
  Console.WriteLine($"pid={pid}");
});

// ── stop ──────────────────────────────────────────────────────────────────────
var stopProjectOpt = new Option<string>("--project")
{
  Description = "Project name",
  Required = true,
};

var stopCommand = new Command("stop", "Stop a running project process");
stopCommand.Add(stopProjectOpt);
stopCommand.SetAction(async (ParseResult result, CancellationToken ct) =>
{
  var project = result.GetValue(stopProjectOpt)!;
  var client = new DaemonClient();
  await client.StopProjectAsync(project, ct);
  Console.WriteLine("stopped");
});

// ── status ────────────────────────────────────────────────────────────────────
var statusProjectOpt = new Option<string>("--project")
{
  Description = "Project name",
  Required = true,
};

var statusCommand = new Command("status", "Report whether a project process is running");
statusCommand.Add(statusProjectOpt);
statusCommand.SetAction(async (ParseResult result, CancellationToken ct) =>
{
  var project = result.GetValue(statusProjectOpt)!;
  var client = new DaemonClient();
  var (running, pid) = await client.GetStatusAsync(project, ct);
  Console.WriteLine(running ? $"running pid={pid}" : "exited");
});

// ── logs ──────────────────────────────────────────────────────────────────────
var logsProjectOpt = new Option<string>("--project")
{
  Description = "Project name",
  Required = true,
};
var logsFromOpt = new Option<long>("--from")
{
  Description = "Byte offset to resume streaming from",
  DefaultValueFactory = _ => 0L,
};

var logsCommand = new Command("logs", "Stream stdout/stderr from a project process (long-running)");
logsCommand.Add(logsProjectOpt);
logsCommand.Add(logsFromOpt);
logsCommand.SetAction(async (ParseResult result, CancellationToken ct) =>
{
  var project = result.GetValue(logsProjectOpt)!;
  var from    = result.GetValue(logsFromOpt);
  var client  = new DaemonClient();

  await client.StreamLogsAsync(project, from, (line, _) =>
  {
    Console.WriteLine(line);
    return Task.CompletedTask;
  }, ct);
});

// ── wire up ───────────────────────────────────────────────────────────────────
rootCommand.Add(daemonCommand);
rootCommand.Add(startCommand);
rootCommand.Add(stopCommand);
rootCommand.Add(statusCommand);
rootCommand.Add(logsCommand);

return await rootCommand.Parse(args).InvokeAsync(new InvocationConfiguration());
