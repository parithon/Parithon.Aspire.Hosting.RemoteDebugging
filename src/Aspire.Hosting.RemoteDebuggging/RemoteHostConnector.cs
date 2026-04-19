using System.Diagnostics;
using System.Net;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteDebuggging;

internal static class RemoteHostConnector
{
  internal static async Task ConnectAsync(RemoteHostResource resource, ResourceNotificationService notifications, ResourceLoggerService loggers, CancellationToken cancellationToken)
  {
    var logger = loggers.GetLogger(resource);

    await notifications.PublishUpdateAsync(resource, s => s with
    {
      State = KnownRemoteResourceStates.ConnectingSnapshot,
      StartTimeStamp = DateTime.UtcNow
    }).ConfigureAwait(false);

    try
    {
      var dns = resource.DnsParameter is not null
        ? await resource.DnsParameter.Resource.GetValueAsync(cancellationToken)
        : resource.Dns;

      if (logger.IsEnabled(LogLevel.Trace))
      {
        logger.LogTrace("Resolving the DNS host name for {Dns}", dns);
      }

      var addresses = await Dns.GetHostAddressesAsync(dns ?? resource.Name, cancellationToken) 
        ?? throw new UnreachableException("Could not determine host address.");

      // TODO: attempt to establish a connection.
      if (logger.IsEnabled(LogLevel.Information))
      {
        logger.LogInformation("Establishing a connection to {Dns} using {IPAddress}", dns, addresses);
      }
      IRemoteHostTransport transport = resource.TransportType switch
      {
        TransportType.SSH => new SshTransport(),
        _ => throw new NotSupportedException()
      };
      await transport.ConnectAsync(resource, logger, cancellationToken);

      resource.Annotations.Add(new RemoteHostTransportAnnotation(transport));

      await notifications.PublishUpdateAsync(resource, s => s with
      {
        State = KnownRemoteResourceStates.ConnectedSnapshot
      }).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
      logger.LogError(ex, "Failed to connect to the remote host {Name}", resource.Name);

      await notifications.PublishUpdateAsync(resource, s => s with
      {
        State = KnownRemoteResourceStates.FailedToConnectSnapshot
      });
    }
  }

  internal static async Task DisconnectAsync(RemoteHostResource resource, ResourceNotificationService notifications, ResourceLoggerService loggers, CancellationToken cancellationToken)
  {
    var logger = loggers.GetLogger(resource);

    await notifications.PublishUpdateAsync(resource, s => s with
    {
      State = KnownRemoteResourceStates.DisconnectingSnapshot
    }).ConfigureAwait(false);

    try
    {
      if (resource.TryGetLastAnnotation<RemoteHostTransportAnnotation>(out var annotation) && annotation is not null)
      {
        await annotation.Transport.DisconnectAsync(resource, logger, cancellationToken);
        annotation.Dispose();
        resource.Annotations.Remove(annotation);
      }

      await notifications.PublishUpdateAsync(resource, s => s with
      {
        State = KnownRemoteResourceStates.DisconnectedSnapshot,
        StopTimeStamp = DateTime.UtcNow
      }).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
      logger.LogError(ex, "Failed to disconnect from the remote host {Name}", resource.Name);

      await notifications.PublishUpdateAsync(resource, s => s with
      {
        State = new ResourceStateSnapshot(KnownResourceStates.Exited, null)
      }).ConfigureAwait(false);
    }
  }
}