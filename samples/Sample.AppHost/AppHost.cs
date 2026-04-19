using System.Runtime.InteropServices;
using Aspire.Hosting.RemoteDebugging;

var builder = DistributedApplication.CreateBuilder(args);

var windev_passwd = builder.AddParameter("win-dev-password", secret: true);

var windev = builder.AddRemoteHost("win-dev", OSPlatform.Windows, new("loadmin", windev_passwd))
  .WithEndpoint("10.47.255.250", TransportType.SSH, 22);

builder.Build().Run();
