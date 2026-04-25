using System.Diagnostics;
using System.Xml.Linq;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteDebugging.RemoteHost.Annotations;
using Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;
using Aspire.Hosting.RemoteDebugging.RemoteProject.Annotations;
using Aspire.Hosting.RemoteDebugging.RemoteProject.HealthChecks;
using Aspire.Hosting.RemoteDebugging.Sidecar;
using Google.Protobuf.Collections;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteDebugging.RemoteProject;

internal static class RemoteProjectRunner
{
  internal static async Task RunAsync<TProject>(RemoteProjectResource<TProject> resource, ResourceNotificationService notifications, ResourceLoggerService loggers, CancellationToken cancellationToken) where TProject : IProjectMetadata
  {
    await resource.RunGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      await RunCoreAsync(resource, notifications, loggers, cancellationToken).ConfigureAwait(false);
    }
    finally
    {
      resource.RunGate.Release();
    }
  }

  internal static async Task StopAsync<TProject>(RemoteProjectResource<TProject> resource, ResourceNotificationService notifications, ResourceLoggerService loggers, CancellationToken cancellationToken) where TProject : IProjectMetadata
  {
    var logger = loggers.GetLogger(resource);

    // Cancel the in-flight run first so StreamLogs unblocks immediately.
    resource.CancelRun();

    // Try to acquire the gate without blocking.
    // If RunAsync is active (gate held), it will handle all state cleanup when cancellation
    // propagates — we only need to send the StopProcess RPC to terminate the remote process.
    // If no run is active we own the gate and must do explicit state cleanup.
    var ownGate = await resource.RunGate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false);
    try
    {
      if (!resource.Parent.TryGetLastAnnotation<RemoteHostTransportAnnotation>(out var annotation) || annotation?.Transport is not IRemoteHostTransport transport)
      {
        if (ownGate)
        {
          resource.RemoteProcessId = null;
          await notifications.PublishUpdateAsync(resource, s => s with
          {
            State = KnownRemoteProjectStates.ExitedSnapshot
          }).ConfigureAwait(false);
        }
        return;
      }

      if (ownGate)
      {
        await notifications.PublishUpdateAsync(resource, s => s with
        {
          State = KnownRemoteProjectStates.StoppingSnapshot
        }).ConfigureAwait(false);
      }

      // Windows Service: stop and uninstall via sc.exe; also stops the log-watcher.
      if (resource.TryGetLastAnnotation<WindowsServiceAnnotation>(out var svcAnnotation) && svcAnnotation is not null)
      {
        try
        {
          using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
          await WindowsServiceRunner.StopAndUninstallAsync(
            resource, svcAnnotation, transport, logger, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
          logger.LogWarning(ex, "Error stopping Windows Service '{Name}'.", resource.Name);
        }
      }
      else if (annotation.Transport.SidecarChannel is not null)
      {
        // Normal process: stop via sidecar RPC.
        try
        {
          var client = new SidecarService.SidecarServiceClient(annotation.Transport.SidecarChannel);
          using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
          using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
          await client.StopProcessAsync(
            new StopProcessRequest { Name = resource.Name },
            cancellationToken: linkedCts.Token).ConfigureAwait(false);

          logger.LogInformation("Remote process '{Name}' stopped.", resource.Name);
        }
        catch (Exception ex) when (ex is RpcException or OperationCanceledException)
        {
          logger.LogWarning(ex, "StopProcess RPC failed for '{Name}'; process may have already exited.", resource.Name);
        }
      }

      if (ownGate)
      {
        resource.RemoteProcessId = null;
        await notifications.PublishUpdateAsync(resource, s => s with
        {
          State = KnownRemoteProjectStates.ExitedSnapshot
        }).ConfigureAwait(false);
      }
    }
    finally
    {
      if (ownGate)
        resource.RunGate.Release();
    }
  }

  private static async Task RunCoreAsync<TProject>(RemoteProjectResource<TProject> resource, ResourceNotificationService notifications, ResourceLoggerService loggers, CancellationToken cancellationToken) where TProject : IProjectMetadata
  {
    var logger = loggers.GetLogger(resource);

    // Check whether the process is still running from a previous session and the
    // remote artifacts are current. If so, re-attach without rebuilding/redeploying.
    if (await TryReconnectAsync(resource, notifications, logger, cancellationToken).ConfigureAwait(false))
      return;

    // Phase 1: Build
    cancellationToken.ThrowIfCancellationRequested();
    await notifications.PublishUpdateAsync(resource, s => s with
    {
      State = KnownRemoteProjectStates.BuildingSnapshot,
      StartTimeStamp = DateTime.UtcNow
    }).ConfigureAwait(false);

    try
    {
      await BuildAsync(resource, logger, cancellationToken).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      logger.LogError(ex, "Failed to build project for {Name}", resource.Name);
      await notifications.PublishUpdateAsync(resource, s => s with
      {
        State = KnownRemoteProjectStates.FailedToBuildSnapshot,
        StopTimeStamp = DateTime.UtcNow
      }).ConfigureAwait(false);
      return;
    }

    // Phase 2: Deploy
    cancellationToken.ThrowIfCancellationRequested();
    await notifications.PublishUpdateAsync(resource, s => s with
    {
      State = KnownRemoteProjectStates.DeployingSnapshot
    }).ConfigureAwait(false);

    try
    {
      await DeployAsync(resource, logger, cancellationToken).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      logger.LogError(ex, "Failed to deploy project for {Name}", resource.Name);
      await notifications.PublishUpdateAsync(resource, s => s with
      {
        State = KnownRemoteProjectStates.FailedToDeploySnapshot,
        StopTimeStamp = DateTime.UtcNow
      }).ConfigureAwait(false);
      return;
    }

    // Phase 3: Start + stream logs (blocks until process exits or cancellation)
    cancellationToken.ThrowIfCancellationRequested();
    await notifications.PublishUpdateAsync(resource, s => s with
    {
      State = KnownRemoteProjectStates.StartingSnapshot
    }).ConfigureAwait(false);

    // Check whether this resource should run as a Windows Service.
    if (resource.TryGetLastAnnotation<WindowsServiceAnnotation>(out var svcAnnotation) && svcAnnotation is not null)
    {
      if (!resource.Parent.TryGetLastAnnotation<RemoteHostTransportAnnotation>(out var transportAnnotation)
        || transportAnnotation?.Transport is not IRemoteHostTransport transport)
        throw new InvalidOperationException($"Cannot install service '{resource.Name}': no active transport.");

      // Phase 3a: Clean up any stale service from a previous session.
      try
      {
        await WindowsServiceRunner.EnsureCleanAsync(resource, svcAnnotation, transport, logger, cancellationToken)
          .ConfigureAwait(false);
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        logger.LogWarning(ex, "Could not clean up stale Windows Service '{Name}' — continuing anyway.", resource.Name);
      }

      // Phase 3b: Install the service.
      var env = BuildEnvironment(resource);

      // Explicitly pin EventLog source = SCM service name.  .NET's EventLogLoggerProvider
      // (activated by AddWindowsService) defaults to the service name when running under SCM.
      // Pinning it here guarantees the watcher.ps1 ProviderName filter always matches.
      env["Logging__EventLog__SourceName"] = svcAnnotation.ServiceName;
      // AddWindowsService() defaults EventLog minimum level to Warning.  Override to
      // Information so the worker's own LogInformation calls reach the EventLog and
      // are captured by watcher.ps1.
      env["Logging__EventLog__LogLevel__Default"] = "Information";
      try
      {
        await WindowsServiceRunner.InstallAsync(resource, svcAnnotation, transport, env, logger, cancellationToken)
          .ConfigureAwait(false);
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        logger.LogError(ex, "Failed to install Windows Service for {Name}", resource.Name);
        await notifications.PublishUpdateAsync(resource, s => s with
        {
          State = KnownRemoteProjectStates.FailedToStartSnapshot,
          StopTimeStamp = DateTime.UtcNow
        }).ConfigureAwait(false);
        return;
      }

      // Phase 3c: Start the service and stream EventLog output.
      await notifications.PublishUpdateAsync(resource, s => s with
      {
        State = KnownRemoteProjectStates.RunningSnapshot,
        StartTimeStamp = DateTime.UtcNow
      }).ConfigureAwait(false);

      try
      {
        await WindowsServiceRunner.StartAndStreamAsync(resource, svcAnnotation, transport, logger, cancellationToken)
          .ConfigureAwait(false);
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        logger.LogError(ex, "Failed to start Windows Service for {Name}", resource.Name);
        await notifications.PublishUpdateAsync(resource, s => s with
        {
          State = KnownRemoteProjectStates.FailedToStartSnapshot,
          StopTimeStamp = DateTime.UtcNow
        }).ConfigureAwait(false);

        // Best-effort cleanup — remove the service even on failure.
        try
        {
          using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
          await WindowsServiceRunner.StopAndUninstallAsync(
            resource, svcAnnotation, transport, logger, cleanupCts.Token).ConfigureAwait(false);
        }
        catch (Exception cleanupEx)
        {
          logger.LogDebug(cleanupEx, "Cleanup after start failure for '{Name}' encountered an error.", resource.Name);
        }
        return;
      }

      // Service exited normally (cancelled). Uninstall (ephemeral lifecycle).
      await notifications.PublishUpdateAsync(resource, s => s with
      {
        State = KnownRemoteProjectStates.StoppingSnapshot,
        StopTimeStamp = DateTime.UtcNow
      }).ConfigureAwait(false);

      try
      {
        using var uninstallCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await WindowsServiceRunner.StopAndUninstallAsync(
          resource, svcAnnotation, transport, logger, uninstallCts.Token).ConfigureAwait(false);
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Error uninstalling Windows Service '{Name}' after stop.", resource.Name);
      }

      await notifications.PublishUpdateAsync(resource, s => s with
      {
        State = KnownRemoteProjectStates.ExitedSnapshot
      }).ConfigureAwait(false);
    }
    else
    {
      try
      {
        await StartAsync(resource, notifications, logger, cancellationToken).ConfigureAwait(false);
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        logger.LogError(ex, "Failed to start project for {Name}", resource.Name);
        await notifications.PublishUpdateAsync(resource, s => s with
        {
          State = KnownRemoteProjectStates.FailedToStartSnapshot,
          StopTimeStamp = DateTime.UtcNow
        }).ConfigureAwait(false);
      }
    }
  }

  /// <summary>
  /// Checks whether the remote process is still running from a previous session and the
  /// deployed artifacts are current (timestamp within ±2s of the local build output).
  /// <para>
  /// If the process is running and artifacts are current, re-attaches to the log stream
  /// with <c>replay_cached: true</c> and returns <see langword="true"/>.
  /// If the process is running but artifacts are stale, stops the remote process and
  /// returns <see langword="false"/> so the caller can rebuild and redeploy.
  /// </para>
  /// </summary>
  private static async Task<bool> TryReconnectAsync<TProject>(
    RemoteProjectResource<TProject> resource,
    ResourceNotificationService notifications,
    ILogger logger,
    CancellationToken cancellationToken) where TProject : IProjectMetadata
  {
    if (!resource.Parent.TryGetLastAnnotation<RemoteHostTransportAnnotation>(out var annotation)
      || annotation?.Transport.SidecarChannel is null)
      return false;

    var channel = annotation.Transport.SidecarChannel;
    var client  = new SidecarService.SidecarServiceClient(channel);

    ListProcessesResponse processes;
    try
    {
      using var pingCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
      processes = await client.ListProcessesAsync(
        new ListProcessesRequest(),
        cancellationToken: pingCts.Token).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is RpcException or OperationCanceledException)
    {
      return false;
    }

    var existing = processes.Processes.FirstOrDefault(p =>
      string.Equals(p.Name, resource.Name, StringComparison.Ordinal)
      && p.State == "Running");

    if (existing is null)
      return false;

    // Process is running — check whether the remote artifacts are still current.
    if (resource.BuildOutputPath is string localDir && resource.AssemblyName is string asmName
      && resource.RemoteDeploymentPath is string remoteBase)
    {
      var localDll  = Path.Combine(localDir, $"{asmName}.dll");
      var remoteDll = $"{remoteBase}/{asmName}.dll";

      var isCurrent = await annotation.Transport
        .IsProjectCurrentAsync(localDll, remoteDll, cancellationToken).ConfigureAwait(false);

      if (!isCurrent)
      {
        logger.LogInformation(
          "Remote process '{Name}' is running but artifacts are outdated; stopping before redeploying.",
          resource.Name);
        try
        {
          using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
          await client.StopProcessAsync(
            new StopProcessRequest { Name = resource.Name },
            cancellationToken: stopCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RpcException or OperationCanceledException)
        {
          logger.LogWarning(ex, "Failed to stop outdated remote process '{Name}'.", resource.Name);
        }
        return false;
      }
    }

    logger.LogInformation(
      "Remote process '{Name}' is still running with current artifacts; re-attaching log stream.",
      resource.Name);

    resource.RemoteProcessId = existing.Pid;

    await notifications.PublishUpdateAsync(resource, s => s with
    {
      State = KnownRemoteProjectStates.RunningSnapshot,
      StartTimeStamp = DateTime.UtcNow
    }).ConfigureAwait(false);

    try
    {
      await StreamLogsAsync(resource, client, logger, replayCached: true, cancellationToken)
        .ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      logger.LogError(ex, "Error streaming logs for re-attached process '{Name}'; falling back to rebuild.", resource.Name);
      resource.RemoteProcessId = null;
      return false;
    }

    await notifications.PublishUpdateAsync(resource, s => s with
    {
      State = KnownRemoteProjectStates.ExitedSnapshot,
      StopTimeStamp = DateTime.UtcNow
    }).ConfigureAwait(false);

    return true;
  }

  private static async Task BuildAsync<TProject>(RemoteProjectResource<TProject> resource, ILogger logger, CancellationToken cancellationToken)  where TProject : IProjectMetadata
  {
    TProject metadata;
    try
    {
      metadata = Activator.CreateInstance<TProject>();
    }
    catch (Exception ex)
    {
      throw new InvalidOperationException(
        $"Cannot create project metadata for '{typeof(TProject).Name}'. " +
        "Ensure the type has a public parameterless constructor (Aspire-generated types satisfy this requirement).",
        ex);
    }

    var projectPath = metadata.ProjectPath;
    var projectDir = Path.GetDirectoryName(projectPath)
      ?? throw new InvalidOperationException(
           $"Cannot determine directory for project path '{projectPath}'.");

    var csprojXml = XDocument.Load(projectPath);

    // Support both <TargetFramework> (single) and <TargetFrameworks> (multi-targeting).
    string tfm;
    var singleTfm = csprojXml.Descendants("TargetFramework").FirstOrDefault()?.Value;
    if (singleTfm is not null)
    {
      tfm = singleTfm;
    }
    else
    {
      var multipleTfms = csprojXml.Descendants("TargetFrameworks").FirstOrDefault()?.Value
        ?? throw new InvalidOperationException(
             $"Cannot determine TargetFramework(s) from '{projectPath}'.");

      var currentTfm = $"net{Environment.Version.Major}.{Environment.Version.Minor}";
      tfm = multipleTfms
        .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault(t => string.Equals(t, currentTfm, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException(
             $"Project '{projectPath}' targets [{multipleTfms}] but none matches the current runtime '{currentTfm}'. " +
             $"Ensure the project includes '{currentTfm}' as a target framework.");
    }

    // Resolve the assembly name: prefer <AssemblyName> in the csproj, fall back to the
    // project file name (without extension), which is the default MSBuild behaviour.
    var assemblyName = csprojXml.Descendants("AssemblyName").FirstOrDefault()?.Value
      ?? Path.GetFileNameWithoutExtension(projectPath);

    if (logger.IsEnabled(LogLevel.Information))
      logger.LogInformation("Building framework-dependent artifact for {Project} ({Tfm})", Path.GetFileName(projectPath), tfm);

    var psi = new ProcessStartInfo("dotnet")
    {
      UseShellExecute = false,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      CreateNoWindow = true,
    };
    psi.ArgumentList.Add("build");
    psi.ArgumentList.Add(projectPath);
    psi.ArgumentList.Add("-c");
    psi.ArgumentList.Add("Debug");
    psi.ArgumentList.Add("-f");
    psi.ArgumentList.Add(tfm);

    if (logger.IsEnabled(LogLevel.Debug))
      logger.LogDebug("Running: dotnet {Args}", string.Join(' ', psi.ArgumentList));

    using var process = new Process { StartInfo = psi };
    process.Start();

    var stdoutTask = DrainStreamAsync(process.StandardOutput, logger, isError: false);
    var stderrTask = DrainStreamAsync(process.StandardError, logger, isError: true);

    try
    {
      await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
      await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
      await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
      await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
      throw;
    }

    if (process.ExitCode != 0)
      throw new InvalidOperationException(
        $"'dotnet build' failed with exit code {process.ExitCode}. See resource logs for details.");

    resource.BuildOutputPath = Path.Combine(projectDir, "bin", "Debug", tfm);
    resource.AssemblyName    = assemblyName;
  }

  /// <summary>Reads lines from <paramref name="reader"/> until EOF, forwarding each to the logger.</summary>
  private static async Task DrainStreamAsync(StreamReader reader, ILogger logger, bool isError)
  {
    string? line;
    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
    {
      if (isError)
        logger.LogWarning("{Line}", line);
      else
        logger.LogInformation("{Line}", line);
    }
  }

  private static async Task DeployAsync<TProject>(RemoteProjectResource<TProject> resource, ILogger logger, CancellationToken cancellationToken) where TProject : IProjectMetadata
  {
    if (resource.BuildOutputPath is not string buildOutput)
      throw new InvalidOperationException($"Cannot deploy '{resource.Name}': BuildOutputPath is not set.");

    if (!resource.Parent.TryGetLastAnnotation<RemoteHostTransportAnnotation>(out var annotation) || annotation is null)
      throw new InvalidOperationException($"Cannot deploy '{resource.Name}': no active transport on the remote host.");

    // Pass the raw DeploymentPath — SshTransport.DeployDirectoryAsync normalises to SFTP format internally.
    var basePath = resource.Parent.DeploymentPath!;
    var remotePath = $"{basePath}/{resource.Name}";
    
    if (logger.IsEnabled(LogLevel.Information))
    {
      logger.LogInformation("Deploying {Name} to {RemotePath}", resource.Name, remotePath);
    }

    await annotation.Transport.DeployDirectoryAsync(buildOutput, remotePath, logger, cancellationToken)
      .ConfigureAwait(false);

    resource.RemoteDeploymentPath = remotePath;
  }

  private static async Task StartAsync<TProject>(
    RemoteProjectResource<TProject> resource,
    ResourceNotificationService notifications,
    ILogger logger,
    CancellationToken cancellationToken) where TProject : IProjectMetadata
  {
    if (resource.RemoteDeploymentPath is not string remotePath)
      throw new InvalidOperationException($"Cannot start '{resource.Name}': RemoteDeploymentPath is not set.");

    if (resource.AssemblyName is not string assemblyName)
      throw new InvalidOperationException($"Cannot start '{resource.Name}': AssemblyName is not set.");

    if (!resource.Parent.TryGetLastAnnotation<RemoteHostTransportAnnotation>(out var annotation)
      || annotation?.Transport.SidecarChannel is null)
      throw new InvalidOperationException($"Cannot start '{resource.Name}': no active sidecar channel.");

    var channel = annotation.Transport.SidecarChannel;
    var client  = new SidecarService.SidecarServiceClient(channel);

    var env = BuildEnvironment(resource);

    var request = new StartProcessRequest
    {
      Name             = resource.Name,
      WorkingDirectory = remotePath,
      EntryPoint       = $"{assemblyName}.dll",
    };
    request.Environment.Add(env);

    StartProcessResponse response;
    try
    {
      response = await client.StartProcessAsync(request, cancellationToken: cancellationToken)
        .ConfigureAwait(false);
    }
    catch (RpcException ex)
    {
      throw new InvalidOperationException(
        $"StartProcess RPC failed for '{resource.Name}': {ex.Status.Detail}", ex);
    }

    resource.RemoteProcessId = response.Pid;

    if (response.AlreadyRunning)
      logger.LogInformation("Remote process '{Name}' was already running (PID {Pid}).", resource.Name, response.Pid);
    else
      logger.LogInformation("Remote process '{Name}' started (PID {Pid}).", resource.Name, response.Pid);

    await notifications.PublishUpdateAsync(resource, s => s with
    {
      State = KnownRemoteProjectStates.RunningSnapshot,
      StartTimeStamp = DateTime.UtcNow
    }).ConfigureAwait(false);

    // Block until the remote process exits or the run is cancelled.
    await StreamLogsAsync(resource, client, logger, replayCached: false, cancellationToken)
      .ConfigureAwait(false);

    resource.RemoteProcessId = null;

    await notifications.PublishUpdateAsync(resource, s => s with
    {
      State = KnownRemoteProjectStates.ExitedSnapshot,
      StopTimeStamp = DateTime.UtcNow
    }).ConfigureAwait(false);
  }

  /// <summary>
  /// Streams stdout/stderr from the sidecar for <paramref name="resource"/> and pipes
  /// each line to the Aspire dashboard log. Blocks until the remote process exits or
  /// <paramref name="cancellationToken"/> is cancelled.
  /// </summary>
  private static async Task StreamLogsAsync<TProject>(
    RemoteProjectResource<TProject> resource,
    SidecarService.SidecarServiceClient client,
    ILogger logger,
    bool replayCached,
    CancellationToken cancellationToken) where TProject : IProjectMetadata
  {
    using var call = client.StreamLogs(
      new StreamLogsRequest { Name = resource.Name, ReplayCached = replayCached },
      cancellationToken: cancellationToken);

    try
    {
      await foreach (var line in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
      {
        if (line.IsError)
          logger.LogError("{Line}", line.Content);
        else
          logger.LogInformation("{Line}", line.Content);
      }
    }
    catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
    {
      // Client cancelled — normal stop path.
    }
    catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
    {
      logger.LogWarning("Process '{Name}' not found on sidecar; it may have exited before streaming started.", resource.Name);
    }
  }

  private static MapField<string, string> BuildEnvironment<TProject>(RemoteProjectResource<TProject> resource) where TProject : IProjectMetadata
  {
    var env = new MapField<string, string>
    {
      { "ASPNETCORE_ENVIRONMENT", "Development" },
      { "DOTNET_ENVIRONMENT",     "Development" },
    };

    // Inject OTEL tunnel defaults so the remote process exports telemetry back to the
    // AppHost via the reverse SSH tunnel. User-provided vars (below) take precedence.
    if (resource.Parent.TryGetLastAnnotation<RemoteHostTransportAnnotation>(out var annotation)
      && annotation?.Transport.OtelTunnelEndpoint is Uri otelEndpoint)
    {
      env["OTEL_EXPORTER_OTLP_ENDPOINT"] = otelEndpoint.ToString().TrimEnd('/');
      env["OTEL_EXPORTER_OTLP_PROTOCOL"] = "grpc";

      if (annotation.Transport.OtelTunnelHeaders is string otlpHeaders)
        env["OTEL_EXPORTER_OTLP_HEADERS"] = otlpHeaders;

      // Set the service name so the Aspire dashboard identifies the resource correctly.
      // Matches the behaviour of DCP-managed resources; user env vars below can override.
      env["OTEL_SERVICE_NAME"] = resource.Name;

      // Mirror the development-mode tuning that Aspire's DCP injects for managed resources.
      // Without these, metrics/traces/logs batch at their SDK defaults (60s / 5s / 5s),
      // making the dashboard appear empty for the first minute of a session.
      env["OTEL_BLRP_SCHEDULE_DELAY"] = "1000";
      env["OTEL_BSP_SCHEDULE_DELAY"] = "1000";
      env["OTEL_METRIC_EXPORT_INTERVAL"] = "1000";
      env["OTEL_TRACES_SAMPLER"] = "always_on";
      env["OTEL_METRICS_EXEMPLAR_FILTER"] = "trace_based";
    }

    foreach (var (key, value) in resource.EnvironmentVariables)
      env[key] = value;

    return env;
  }
}

