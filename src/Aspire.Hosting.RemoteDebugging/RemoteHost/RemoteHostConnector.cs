using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteDebugging.RemoteHost.Annotations;
using Aspire.Hosting.RemoteDebugging.RemoteHost.HealthChecks;
using Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteDebugging.RemoteHost;

internal static class RemoteHostConnector
{
  internal static async Task ConnectAsync(RemoteHostResource resource, ResourceNotificationService notifications, ResourceLoggerService loggers, CancellationToken cancellationToken)
  {
    await resource.ConnectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      await ConnectCoreAsync(resource, notifications, loggers, cancellationToken).ConfigureAwait(false);
    }
    finally
    {
      resource.ConnectGate.Release();
    }
  }

  private static async Task ConnectCoreAsync(RemoteHostResource resource, ResourceNotificationService notifications, ResourceLoggerService loggers, CancellationToken cancellationToken)
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

      // Subscribe to transport events BEFORE starting vsdbg so no exit event is missed.
      SubscribeTransportEvents(transport, resource, notifications, loggers, cancellationToken);

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

      // Transition through Running to start Aspire's health check loop, then settle on Connected.
      await notifications.PublishUpdateAsync(resource, s => s with
      {
        State = KnownRemoteResourceStates.RunningSnapshot
      }).ConfigureAwait(false);

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

  private static void SubscribeTransportEvents(
    IRemoteHostTransport transport,
    RemoteHostResource resource,
    ResourceNotificationService notifications,
    ResourceLoggerService loggers,
    CancellationToken cancellationToken)
  {
    transport.RemoteDebuggerExited += (_, _) =>
    {
      _ = Task.Run(async () =>
      {
        var log = loggers.GetLogger(resource);
        try
        {
          log.LogWarning("vsdbg exited unexpectedly. Attempting restart...");

          var restarted = await transport.StartRemoteDebugger(log, CancellationToken.None).ConfigureAwait(false);
          if (restarted)
          {
            log.LogInformation("vsdbg restarted successfully.");
            await notifications.PublishUpdateAsync(resource, s => s with
            {
              State = KnownRemoteResourceStates.RunningSnapshot
            }).ConfigureAwait(false);
            await notifications.PublishUpdateAsync(resource, s => s with
            {
              State = KnownRemoteResourceStates.ConnectedSnapshot
            }).ConfigureAwait(false);
          }
          else
          {
            log.LogError("vsdbg could not be restarted.");
            await notifications.PublishUpdateAsync(resource, s => s with
            {
              State = new ResourceStateSnapshot(KnownResourceStates.Exited, KnownResourceStateStyles.Error),
              StopTimeStamp = DateTime.UtcNow
            }).ConfigureAwait(false);
          }
        }
        catch (Exception ex)
        {
          log.LogError(ex, "Unhandled exception while restarting vsdbg for {Name}.", resource.Name);
        }
      }, CancellationToken.None);
    };

    transport.ConnectionDropped += (_, _) =>
    {
      _ = Task.Run(async () =>
      {
        var log = loggers.GetLogger(resource);
        try
        {
          log.LogError("SSH connection to {Name} was lost. Reconnecting in 5 seconds...", resource.Name);

          await notifications.PublishUpdateAsync(resource, s => s with
          {
            State = KnownRemoteResourceStates.ReconnectingSnapshot
          }).ConfigureAwait(false);

          await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
          await ConnectAsync(resource, notifications, loggers, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
          log.LogError(ex, "Reconnect attempt for {Name} failed.", resource.Name);
        }
      }, CancellationToken.None);
    };
  }

  internal static async Task DisconnectAsync(RemoteHostResource resource, ResourceNotificationService notifications, ResourceLoggerService loggers, CancellationToken cancellationToken)
  {
    await resource.ConnectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      await DisconnectCoreAsync(resource, notifications, loggers, cancellationToken).ConfigureAwait(false);
    }
    finally
    {
      resource.ConnectGate.Release();
    }
  }

  private static async Task DisconnectCoreAsync(RemoteHostResource resource, ResourceNotificationService notifications, ResourceLoggerService loggers, CancellationToken cancellationToken)
  {
    var logger = loggers.GetLogger(resource);

    await notifications.PublishUpdateAsync(resource, s => s with
    {
      State = KnownRemoteResourceStates.ExitedSnapshot
    }).ConfigureAwait(false);

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