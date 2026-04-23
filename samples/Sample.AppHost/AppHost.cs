using System.Runtime.InteropServices;
using Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var windev_passwd = builder.AddParameter("win-dev-password", secret: true);

var windev = builder.AddRemoteHost("win-dev", OSPlatform.Windows, new("loadmin", windev_passwd))
  .WithEndpoint("10.47.255.250", TransportType.SSH, 22);

builder.AddRemoteProject<Sample_WorkerApp>("remote-worker", windev);

// builder.AddProject<Sample_WorkerApp>("local-worker");

builder.Build().Run();
