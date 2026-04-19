using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteDebuggging;

internal interface IRemoteHostTransport : IDisposable
{
  Task ConnectAsync(RemoteHostResource resource, ILogger logger, CancellationToken cancellationToken);
  Task DisconnectAsync(RemoteHostResource resource, ILogger logger, CancellationToken cancellationToken);
}
