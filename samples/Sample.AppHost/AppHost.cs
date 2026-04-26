using System.Runtime.InteropServices;
using Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var windev_passwd = builder.AddParameter("win-dev-password", secret: true);

var windev = builder.AddRemoteHost("win-dev", OSPlatform.Windows, new("loadmin", windev_passwd))
  .WithEndpoint("10.47.255.250", TransportType.SSH, 22);

builder.AddRemoteProject<Sample_WorkerApp>("remote-worker", windev)
  .AsWindowsService("remoteworker", "Remote Worker")
  .WithLoggingSupport(
    @"C:\Windows\Logs\remote-worker\worker.log",
    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
  .WithEnvironment("service-mode", "windows");

builder.Build().Run();
