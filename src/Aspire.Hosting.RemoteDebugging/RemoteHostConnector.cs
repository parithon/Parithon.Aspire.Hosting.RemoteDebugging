using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteDebugging;

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
      // Dispose any stale transport from a previous connect attempt before creating a new one.
      if (resource.TryGetLastAnnotation<RemoteHostTransportAnnotation>(out var existing) && existing is not null)
      {
        existing.Dispose();
        resource.Annotations.Remove(existing);
      }

      IRemoteHostTransport transport = resource.TransportType switch
      {
        TransportType.SSH => new SshTransport(),
        _ => throw new NotSupportedException($"Transport type '{resource.TransportType}' is not supported.")
      };

      if (logger.IsEnabled(LogLevel.Information))
      {
        logger.LogInformation("Establishing a {TransportType} connection to {Resource}", resource.TransportType, resource.Name);
      }

      await transport.ConnectAsync(resource, logger, cancellationToken).ConfigureAwait(false);
      resource.Annotations.Add(new RemoteHostTransportAnnotation(transport));

      await notifications.PublishUpdateAsync(resource, s => s with
      {
        State = KnownRemoteResourceStates.InstallRemoteDebuggerSnapshot
      }).ConfigureAwait(false);
      
      var vsdbResult = await transport.InstallRemoteDebugger(logger, cancellationToken);

      if (!vsdbResult.IsInstalled)
      {
        await notifications.PublishUpdateAsync(resource, s => s with
        {
          State = KnownRemoteResourceStates.FailedToInitializeSnapshot,
          StopTimeStamp = DateTime.UtcNow
        }).ConfigureAwait(false);
        return;
      }

      var started = await transport.StartRemoteDebugger(logger, cancellationToken);
      if (!started)
      {
        await notifications.PublishUpdateAsync(resource, s => s with
        {
          State = new ResourceStateSnapshot(KnownResourceStates.Exited, KnownResourceStateStyles.Error),
          StopTimeStamp = DateTime.UtcNow
        }).ConfigureAwait(false);
        return;
      }

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
      }).ConfigureAwait(false);
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
        await annotation.Transport.DisconnectAsync(logger, cancellationToken).ConfigureAwait(false);
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