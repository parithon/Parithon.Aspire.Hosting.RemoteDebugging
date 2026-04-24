using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteDebugging.RemoteHost.Annotations;
using Aspire.Hosting.RemoteDebugging.RemoteHost.HealthChecks;
using Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.RemoteDebugging.RemoteHost;

internal static class RemoteHostConnector
{
  internal static async Task ConnectAsync(RemoteHostResource resource, ResourceNotificationService notifications, ResourceLoggerService loggers, IServiceProvider services, CancellationToken cancellationToken, bool isReconnect = false)
  {
    await resource.ConnectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      await ConnectCoreAsync(resource, notifications, loggers, services, cancellationToken, isReconnect).ConfigureAwait(false);
    }
    finally
    {
      resource.ConnectGate.Release();
    }
  }

  private static async Task ConnectCoreAsync(RemoteHostResource resource, ResourceNotificationService notifications, ResourceLoggerService loggers, IServiceProvider services, CancellationToken cancellationToken, bool isReconnect = false)
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

      // Check whether the sidecar on the remote host is current, outdated, or absent.
      var deploymentStatus = await transport.SidecarDeployedAsync(resource, cancellationToken)
        .ConfigureAwait(false);

      if (deploymentStatus == SidecarDeploymentStatus.UpToDate)
      {
        logger.LogDebug("Sidecar on remote host is up to date — skipping upload.");
      }
      else
      {
        if (deploymentStatus == SidecarDeploymentStatus.Outdated)
        {
          // A stale sidecar is present. If it is still running its DLL will be locked and the
          // directory clean in DeployDirectoryAsync will fail. Shut it down gracefully first.
          logger.LogInformation("Sidecar on remote host is outdated; shutting down any running instance before redeploying.");
          await transport.ShutdownRunningSidecarAsync(resource, logger, cancellationToken)
            .ConfigureAwait(false);
        }

        await notifications.PublishUpdateAsync(resource, s => s with
        {
          State = KnownRemoteResourceStates.DeployingSidecarSnapshot
        }).ConfigureAwait(false);

        var localSidecarDir = Path.Combine(AppContext.BaseDirectory, "sidecar");
        if (!Directory.Exists(localSidecarDir))
          throw new InvalidOperationException(
            $"Sidecar directory not found at '{localSidecarDir}'. Ensure the Aspire.Hosting.RemoteDebugging package has been built.");

        var remoteSidecarPath = $"{resource.DeploymentPath}/sidecar";
        logger.LogInformation(
          deploymentStatus == SidecarDeploymentStatus.Outdated
            ? "Redeploying updated sidecar to {RemotePath}"
            : "Deploying sidecar to {RemotePath}",
          remoteSidecarPath);

        await transport.DeployDirectoryAsync(localSidecarDir, remoteSidecarPath, logger, cancellationToken)
          .ConfigureAwait(false);
      }

      // Start the sidecar process and establish the gRPC tunnel.
      await notifications.PublishUpdateAsync(resource, s => s with
      {
        State = KnownRemoteResourceStates.StartingSidecarSnapshot
      }).ConfigureAwait(false);

      await transport.StartSidecarAsync(resource, logger, isReconnect, cancellationToken).ConfigureAwait(false);

      var (otlpEndpointUrl, otlpApiKey) = ResolveOtlpConfig(services);
      await transport.StartOtelTunnelAsync(otlpEndpointUrl, otlpApiKey, logger, cancellationToken).ConfigureAwait(false);

      await notifications.PublishUpdateAsync(resource, s => s with
      {
        State = KnownRemoteResourceStates.InstallingToolsSnapshot
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
      SubscribeTransportEvents(transport, resource, notifications, loggers, services, cancellationToken);

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
    IServiceProvider services,
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
          await ConnectAsync(resource, notifications, loggers, services, cancellationToken, isReconnect: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
          log.LogError(ex, "Reconnect attempt for {Name} failed.", resource.Name);
        }
      }, CancellationToken.None);
    };
  }

  /// <summary>
  /// Resolves the OTLP endpoint URL and API key from the Aspire AppHost's DI container.
  /// Reads <c>DashboardOptions.OtlpGrpcEndpointUrl</c> and <c>DashboardOptions.OtlpApiKey</c>
  /// via reflection to avoid a hard compile-time dependency on the internal Aspire type.
  /// Falls back to <c>IConfiguration</c> keys and the well-known default port (18889).
  /// </summary>
  private static (string? EndpointUrl, string? ApiKey) ResolveOtlpConfig(IServiceProvider services)
  {
    string? endpointUrl = null;
    string? apiKey = null;

    try
    {
      // DashboardOptions is an internal type in Aspire.Hosting. Resolve it via IOptions<T> using
      // reflection so we don't need an InternalsVisibleTo or a hard dependency on internal APIs.
      var asm = typeof(ResourceNotificationService).Assembly;
      var dashboardOptionsType = asm.GetType("Aspire.Hosting.Dashboard.DashboardOptions");
      if (dashboardOptionsType is not null)
      {
        var ioptionsType = typeof(IOptions<>).MakeGenericType(dashboardOptionsType);
        var optionsInstance = services.GetService(ioptionsType);
        if (optionsInstance is not null)
        {
          var value = ioptionsType.GetProperty("Value")?.GetValue(optionsInstance);
          if (value is not null)
          {
            endpointUrl = dashboardOptionsType.GetProperty("OtlpGrpcEndpointUrl")?.GetValue(value) as string;
            apiKey = dashboardOptionsType.GetProperty("OtlpApiKey")?.GetValue(value) as string;
          }
        }
      }
    }
    catch
    {
      // Best-effort; fall through to IConfiguration fallback below.
    }

    // IConfiguration fallback: Aspire 13.x uses ASPIRE_ prefix; earlier versions used DOTNET_.
    if (string.IsNullOrWhiteSpace(endpointUrl))
    {
      var config = services.GetService<IConfiguration>();
      endpointUrl = config?["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]
        ?? config?["DOTNET_DASHBOARD_OTLP_ENDPOINT_URL"]
        ?? "http://localhost:18889";
    }

    var formattedApiKey = string.IsNullOrWhiteSpace(apiKey)
      ? null
      : $"x-otlp-api-key={apiKey}";

    return (endpointUrl, formattedApiKey);
  }

  internal static async Task DisconnectAsync(RemoteHostResource resource, ResourceNotificationService notifications, ResourceLoggerService loggers, CancellationToken cancellationToken, bool sendShutdown = false)
  {
    await resource.ConnectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      await DisconnectCoreAsync(resource, notifications, loggers, cancellationToken, sendShutdown).ConfigureAwait(false);
    }
    finally
    {
      resource.ConnectGate.Release();
    }
  }

  private static async Task DisconnectCoreAsync(RemoteHostResource resource, ResourceNotificationService notifications, ResourceLoggerService loggers, CancellationToken cancellationToken, bool sendShutdown = false)
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
        await annotation.Transport.DisconnectAsync(logger, cancellationToken, sendShutdown).ConfigureAwait(false);
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