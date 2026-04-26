using System.Runtime.InteropServices;
using Aspire.Hosting.ApplicationModel;

namespace Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost.Annotations;

public class RemoteHostOSPlatformAnnotation(OSPlatform platform) : IResourceAnnotation
{
  public OSPlatform Platform => platform;
}
