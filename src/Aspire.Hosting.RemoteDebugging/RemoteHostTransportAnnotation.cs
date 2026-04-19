using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.RemoteDebugging;

internal sealed class RemoteHostTransportAnnotation(IRemoteHostTransport transport) : IResourceAnnotation, IDisposable
{
  public IRemoteHostTransport Transport => transport;

  public void Dispose()
  {
    if (transport is IDisposable d)
      d.Dispose();
  }
}
