using System.Runtime.InteropServices;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.RemoteDebugging;

public class RemoteHostOSPlatformAnnotation(OSPlatform platform) : IResourceAnnotation
{
  public OSPlatform Platform => platform;
}
