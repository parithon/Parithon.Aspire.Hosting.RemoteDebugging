using System.Runtime.InteropServices;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteDebugging.RemoteHost;
using Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;
using Aspire.Hosting.RemoteDebugging.RemoteProject.Annotations;
using Aspire.Hosting.RemoteDebugging.Sidecar;
using Google.Protobuf.Collections;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteDebugging.RemoteProject;

/// <summary>
/// Manages the lifecycle of a Windows Service on a remote host for an Aspire resource
/// that carries a <see cref="WindowsServiceAnnotation"/>.
/// </summary>
/// <remarks>
/// Lifecycle (ephemeral — install on start, remove on stop):
/// <list type="number">
///   <item><see cref="EnsureCleanAsync"/> — removes any stale service left by a previous AppHost session.</item>
///   <item><see cref="InstallAsync"/> — installs the service and injects env vars via the registry.</item>
///   <item><see cref="StartAndStreamAsync"/> — starts the service and streams an EventLog watcher via the sidecar until cancelled.</item>
///   <item><see cref="StopAndUninstallAsync"/> — stops the service, stops the log-watcher, and removes the service.</item>
/// </list>
/// </remarks>
internal static class WindowsServiceRunner
{
  // ── Public entry points ───────────────────────────────────────────────────

  /// <summary>
  /// Removes any service from the previous AppHost session that may have been left behind
  /// due to a crash. Called during the connect phase before installing the new service.
  /// </summary>
  public static async Task EnsureCleanAsync<TProject>(
    RemoteProjectResource<TProject> resource,
    WindowsServiceAnnotation annotation,
    IRemoteHostTransport transport,
    ILogger logger,
    CancellationToken cancellationToken) where TProject : IProjectMetadata
  {
    var sn = annotation.ServiceName;

    // Query the service; exit code 1060 means it does not exist — that is the happy path.
    var (exit, _, _) = await transport.ExecuteSshCommandAsync(
      $"sc.exe query {sn}", cancellationToken).ConfigureAwait(false);

    if (exit == 1060)
    {
      logger.LogDebug("Windows Service '{ServiceName}' not found — no cleanup needed.", sn);
      return;
    }

    logger.LogWarning(
      "Stale Windows Service '{ServiceName}' found from a previous session. Stopping and removing it.",
      sn);

    // Stop (ignore errors — it may already be stopped).
    await transport.ExecuteSshCommandAsync($"sc.exe stop {sn}", cancellationToken).ConfigureAwait(false);
    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);

    // Delete.
    var (delExit, _, delErr) = await transport.ExecuteSshCommandAsync(
      $"sc.exe delete {sn}", cancellationToken).ConfigureAwait(false);

    if (delExit != 0)
      logger.LogWarning("sc.exe delete '{ServiceName}' exited {Code}: {Error}", sn, delExit, delErr.Trim());
    else
      logger.LogInformation("Stale Windows Service '{ServiceName}' removed.", sn);
  }

  /// <summary>
  /// Creates the Windows Service and sets its environment variables in the registry.
  /// </summary>
  public static async Task InstallAsync<TProject>(
    RemoteProjectResource<TProject> resource,
    WindowsServiceAnnotation annotation,
    IRemoteHostTransport transport,
    MapField<string, string> environment,
    ILogger logger,
    CancellationToken cancellationToken) where TProject : IProjectMetadata
  {
    if (resource.RemoteDeploymentPath is not string remotePath)
      throw new InvalidOperationException($"Cannot install service '{resource.Name}': RemoteDeploymentPath is not set.");

    if (resource.AssemblyName is not string assemblyName)
      throw new InvalidOperationException($"Cannot install service '{resource.Name}': AssemblyName is not set.");

    var sn = annotation.ServiceName;

    // Resolve dotnet.exe full path so LocalSystem's PATH does not matter.
    var (_, dotnetPath, _) = await transport.ExecuteSshCommandAsync(
      @"powershell.exe -NonInteractive -Command ""(Get-Command dotnet -ErrorAction SilentlyContinue)?.Source""",
      cancellationToken).ConfigureAwait(false);

    var dotnetExe = dotnetPath.Trim();
    if (string.IsNullOrEmpty(dotnetExe))
      dotnetExe = "dotnet.exe"; // fall back to PATH

    // Build the service binary path.  sc.exe requires the full path quoted if it contains spaces.
    var dllPath   = $@"{remotePath}\{assemblyName}.dll";
    var binPath   = $"\"{dotnetExe}\" \"{dllPath}\"";

    var displayName = annotation.DisplayName ?? resource.Name;
    var description = annotation.Description ?? $"Aspire remote project: {resource.Name}";

    // Create the service.
    var createCmd = $@"sc.exe create {sn} binPath= ""{binPath}"" start= demand DisplayName= ""{displayName}""";
    var (createExit, _, createErr) = await transport.ExecuteSshCommandAsync(createCmd, cancellationToken).ConfigureAwait(false);
    if (createExit != 0)
      throw new InvalidOperationException(
        $"Failed to create Windows Service '{sn}' (exit {createExit}): {createErr.Trim()}");

    logger.LogInformation("Windows Service '{ServiceName}' created.", sn);

    // Set description.
    await transport.ExecuteSshCommandAsync(
      $"sc.exe description {sn} \"{description}\"", cancellationToken).ConfigureAwait(false);

    // Write env vars to the service registry key so they are available to LocalSystem.
    if (environment.Count > 0)
    {
      var regValues = string.Join(
        ',',
        environment.Select(kv => $"\"{kv.Key}={EscapePsString(kv.Value)}\""));

      var regCmd =
        $@"powershell.exe -NonInteractive -Command ""Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\{sn}' -Name 'Environment' -Value @({regValues}) -Type MultiString""";

      var (regExit, _, regErr) = await transport.ExecuteSshCommandAsync(regCmd, cancellationToken).ConfigureAwait(false);
      if (regExit != 0)
        logger.LogWarning(
          "Failed to set environment variables for service '{ServiceName}' (exit {Code}): {Error}",
          sn, regExit, regErr.Trim());
      else
        logger.LogDebug("Set {Count} environment variable(s) for service '{ServiceName}'.", environment.Count, sn);
    }

    // Upload the EventLog log-watcher script.
    await UploadLogWatcherScriptAsync(annotation, remotePath, transport, cancellationToken).ConfigureAwait(false);

    logger.LogInformation("Windows Service '{ServiceName}' installed at '{BinPath}'.", sn, binPath);
  }

  /// <summary>
  /// Starts the Windows Service and, via the sidecar, an EventLog watcher that forwards
  /// Application Event Log entries to the Aspire dashboard.  Blocks until
  /// <paramref name="cancellationToken"/> is cancelled (i.e. until the service is stopped).
  /// </summary>
  public static async Task StartAndStreamAsync<TProject>(
    RemoteProjectResource<TProject> resource,
    WindowsServiceAnnotation annotation,
    IRemoteHostTransport transport,
    ILogger logger,
    CancellationToken cancellationToken) where TProject : IProjectMetadata
  {
    if (resource.RemoteDeploymentPath is not string remotePath)
      throw new InvalidOperationException($"Cannot start service '{resource.Name}': RemoteDeploymentPath is not set.");

    var sn = annotation.ServiceName;

    // Start the SCM service.
    var (startExit, _, startErr) = await transport.ExecuteSshCommandAsync(
      $"sc.exe start {sn}", cancellationToken).ConfigureAwait(false);
    if (startExit != 0)
      throw new InvalidOperationException($"Failed to start Windows Service '{sn}' (exit {startExit}): {startErr.Trim()}");

    logger.LogInformation("Windows Service '{ServiceName}' started.", sn);

    if (transport.SidecarChannel is null)
    {
      logger.LogWarning("No sidecar channel — EventLog watcher will not be started for '{ServiceName}'.", sn);
      await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
      return;
    }

    // Start the EventLog watcher process via the sidecar.  The sidecar streams its
    // stdout/stderr back to the Aspire dashboard under the log-watcher process name.
    var watcherScript = $@"{remotePath}\watcher.ps1";
    var client = new SidecarService.SidecarServiceClient(transport.SidecarChannel);

    var request = new StartProcessRequest
    {
      Name             = annotation.LogWatcherProcessName,
      WorkingDirectory = remotePath,
      EntryPoint       = $@"-NonInteractive -File ""{watcherScript}""",
      Executable       = "powershell.exe",
    };

    try
    {
      var response = await client.StartProcessAsync(request, cancellationToken: cancellationToken)
        .ConfigureAwait(false);
      logger.LogDebug("EventLog watcher started (PID {Pid}).", response.Pid);
    }
    catch (RpcException ex)
    {
      logger.LogWarning(ex, "Failed to start EventLog watcher for '{ServiceName}' — console logs will not appear.", sn);
    }

    // Stream the watcher's output until the run is cancelled.
    await StreamWatcherLogsAsync(annotation, client, logger, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Stops the Windows Service and the sidecar log-watcher, then removes (uninstalls) the service.
  /// </summary>
  public static async Task StopAndUninstallAsync<TProject>(
    RemoteProjectResource<TProject> resource,
    WindowsServiceAnnotation annotation,
    IRemoteHostTransport transport,
    ILogger logger,
    CancellationToken cancellationToken) where TProject : IProjectMetadata
  {
    var sn = annotation.ServiceName;

    // Stop the sidecar log-watcher first (best-effort).
    if (transport.SidecarChannel is GrpcChannel channel)
    {
      try
      {
        var client = new SidecarService.SidecarServiceClient(channel);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.StopProcessAsync(
          new StopProcessRequest { Name = annotation.LogWatcherProcessName },
          cancellationToken: cts.Token).ConfigureAwait(false);
      }
      catch (Exception ex) when (ex is RpcException or OperationCanceledException)
      {
        logger.LogDebug(ex, "Could not stop EventLog watcher for '{ServiceName}' (non-fatal).", sn);
      }
    }

    // Stop the Windows Service (ignore errors — it may have already stopped).
    var (stopExit, _, stopErr) = await transport.ExecuteSshCommandAsync(
      $"sc.exe stop {sn}", cancellationToken).ConfigureAwait(false);
    if (stopExit != 0)
      logger.LogDebug("sc.exe stop '{ServiceName}' exited {Code}: {Error}", sn, stopExit, stopErr.Trim());
    else
      logger.LogInformation("Windows Service '{ServiceName}' stopped.", sn);

    // Brief grace period for the SCM to finish the stop transition.
    await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);

    // Uninstall the service.
    var (delExit, _, delErr) = await transport.ExecuteSshCommandAsync(
      $"sc.exe delete {sn}", CancellationToken.None).ConfigureAwait(false);
    if (delExit != 0)
      logger.LogWarning("sc.exe delete '{ServiceName}' exited {Code}: {Error}", sn, delExit, delErr.Trim());
    else
      logger.LogInformation("Windows Service '{ServiceName}' removed.", sn);
  }

  // ── Private helpers ───────────────────────────────────────────────────────

  /// <summary>
  /// Streams stdout from the sidecar EventLog-watcher process to the Aspire dashboard.
  /// Blocks until the watcher exits or the token is cancelled.
  /// </summary>
  private static async Task StreamWatcherLogsAsync(
    WindowsServiceAnnotation annotation,
    SidecarService.SidecarServiceClient client,
    ILogger logger,
    CancellationToken cancellationToken)
  {
    using var call = client.StreamLogs(
      new StreamLogsRequest { Name = annotation.LogWatcherProcessName, ReplayCached = false },
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
      // Normal — cancellation token fired (AppHost stopping or user clicked Stop).
    }
    catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
    {
      logger.LogDebug("EventLog watcher process not found on sidecar — it may have already exited.");
    }
  }

  /// <summary>
  /// Generates and uploads <c>watcher.ps1</c> to the remote deployment directory.
  /// The script polls the Windows Application Event Log for entries whose Source matches
  /// the service name and writes them to stdout so the sidecar can capture them.
  /// </summary>
  private static async Task UploadLogWatcherScriptAsync(
    WindowsServiceAnnotation annotation,
    string remotePath,
    IRemoteHostTransport transport,
    CancellationToken cancellationToken)
  {
    var sn = annotation.ServiceName;
    var script = new StringBuilder();
    script.AppendLine("# Auto-generated EventLog watcher — do not edit.");
    script.AppendLine($"$source = '{sn}'");
    script.AppendLine("$after  = [datetime]::UtcNow");
    script.AppendLine("while ($true) {");
    script.AppendLine("  try {");
    script.AppendLine("    $events = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = $source; StartTime = $after } -ErrorAction SilentlyContinue");
    script.AppendLine("    if ($events) {");
    script.AppendLine("      $events | Sort-Object TimeCreated | ForEach-Object {");
    script.AppendLine("        $after = $_.TimeCreated.AddMilliseconds(1)");
    script.AppendLine("        Write-Host \"[$($_.LevelDisplayName)] $($_.Message)\"");
    script.AppendLine("      }");
    script.AppendLine("    }");
    script.AppendLine("  } catch { }");
    script.AppendLine("  Start-Sleep -Milliseconds 500");
    script.AppendLine("}");

    var remoteScriptPath = $@"{remotePath}\watcher.ps1";
    await transport.UploadTextAsync(script.ToString(), remoteScriptPath, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Escapes a string value for safe embedding inside a PowerShell single-quoted string.</summary>
  private static string EscapePsString(string value) => value.Replace("'", "''");
}
