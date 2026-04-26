using Aspire.Hosting.ApplicationModel;
using Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;

namespace Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost.Annotations;

internal sealed class RemoteHostTransportAnnotation(IRemoteHostTransport transport) : IResourceAnnotation, IDisposable
{
  public IRemoteHostTransport Transport => transport;

  public void Dispose()
  {
    if (transport is IDisposable d)
      d.Dispose();
  }
}
