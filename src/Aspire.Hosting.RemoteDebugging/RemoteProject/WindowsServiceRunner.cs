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

    // Always clean up any stale log-watcher process the sidecar may have retained
    // from a previous session (the sidecar keeps running between AppHost restarts).
    await EnsureLogWatcherStoppedAsync(annotation, transport, logger).ConfigureAwait(false);

    // Query the service.
    // NOTE: sc.exe exit codes are NOT reliably propagated through PowerShell (PowerShell
    // exits 0 regardless). We must check both the exit code AND the text output.
    var (exit, output, error) = await transport.ExecuteSshCommandAsync(
      $"sc.exe query {sn}", cancellationToken).ConfigureAwait(false);

    if (ScServiceNotFound(exit, output, error))
    {
      logger.LogDebug("Windows Service '{ServiceName}' not found — no cleanup needed.", sn);
      return;
    }

    logger.LogWarning(
      "Stale Windows Service '{ServiceName}' found from a previous session. Stopping and removing it.",
      sn);

    // Stop (ignore errors — it may already be stopped).
    await transport.ExecuteSshCommandAsync($"sc.exe stop {sn}", cancellationToken).ConfigureAwait(false);

    // Poll until the service reaches STOPPED (or is not found) before attempting delete.
    await WaitForServiceStoppedAsync(sn, transport, logger, cancellationToken).ConfigureAwait(false);

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
    // We upload a .ps1 script via SFTP and execute it with -File to avoid SSH quoting issues
    // (double-quotes inside -Command are stripped by the SSH shell on Windows).
    //
    // Pre-register the EventLog source under the assembly name.  .NET's EventLogLoggerProvider
    // defaults EventLogSettings.SourceName to IHostEnvironment.ApplicationName (i.e. the
    // entry-assembly name), which in practice equals the project/DLL name — NOT the SCM
    // service name.  EventLogSettings is NOT bound from IConfiguration, so injecting
    // Logging__EventLog__SourceName would have no effect.  We therefore pre-register the
    // source under the assembly name and point the watcher at the same name.
    // Creating a source requires admin rights — the same rights needed for sc.exe create.
    var envScript = new StringBuilder();
    envScript.AppendLine($@"$regPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\{sn}'");
    envScript.AppendLine($"# Pre-register EventLog source under the assembly name so .NET EventLogLoggerProvider can write events.");
    envScript.AppendLine($"if (-not [System.Diagnostics.EventLog]::SourceExists('{EscapePsString(assemblyName)}')) {{");
    envScript.AppendLine($"    [System.Diagnostics.EventLog]::CreateEventSource('{EscapePsString(assemblyName)}', 'Application')");
    envScript.AppendLine("}");
    if (environment.Count > 0)
    {
      envScript.AppendLine("$values  = @(");
      foreach (var kv in environment)
        envScript.AppendLine($"  '{kv.Key}={EscapePsString(kv.Value)}'");
      envScript.AppendLine(")");
      envScript.AppendLine("Set-ItemProperty -Path $regPath -Name 'Environment' -Value $values -Type MultiString");
    }

    var envScriptPath = $@"{remotePath}\set-env.ps1";
    await transport.UploadTextAsync(envScript.ToString(), envScriptPath, cancellationToken).ConfigureAwait(false);

    var (regExit, _, regErr) = await transport.ExecuteSshCommandAsync(
      $@"powershell.exe -NonInteractive -ExecutionPolicy Bypass -File ""{envScriptPath}""",
      cancellationToken).ConfigureAwait(false);

    if (regExit != 0)
      logger.LogWarning(
        "Failed to configure service '{ServiceName}' (exit {Code}): {Error}",
        sn, regExit, regErr.Trim());
    else
      logger.LogDebug("Configured service '{ServiceName}' ({Count} env var(s), EventLog source registered).", sn, environment.Count);

    // Upload the EventLog log-watcher script, filtering by the assembly name (the source
    // .NET's EventLogLoggerProvider defaults to — IHostEnvironment.ApplicationName).
    await UploadLogWatcherScriptAsync(annotation, assemblyName, remotePath, transport, cancellationToken).ConfigureAwait(false);

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
  public static Task StopAndUninstallAsync<TProject>(
    RemoteProjectResource<TProject> resource,
    WindowsServiceAnnotation annotation,
    IRemoteHostTransport transport,
    ILogger logger,
    CancellationToken cancellationToken) where TProject : IProjectMetadata
    => StopAndUninstallAsync(annotation, transport, logger, cancellationToken);

  /// <summary>
  /// Stops the Windows Service and the sidecar log-watcher, then removes (uninstalls) the service.
  /// Called by <see cref="RemoteHostShutdownService"/> during AppHost shutdown where no typed
  /// <see cref="RemoteProjectResource{TProject}"/> is available.
  /// </summary>
  internal static async Task StopAndUninstallAsync(
    WindowsServiceAnnotation annotation,
    IRemoteHostTransport transport,
    ILogger logger,
    CancellationToken cancellationToken)
  {
    var sn = annotation.ServiceName;

    // Stop the sidecar log-watcher first (best-effort).
    await EnsureLogWatcherStoppedAsync(annotation, transport, logger).ConfigureAwait(false);

    // Stop the Windows Service (ignore errors — it may have already stopped).
    var (stopExit, _, stopErr) = await transport.ExecuteSshCommandAsync(
      $"sc.exe stop {sn}", cancellationToken).ConfigureAwait(false);
    if (stopExit != 0)
      logger.LogDebug("sc.exe stop '{ServiceName}' exited {Code}: {Error}", sn, stopExit, stopErr.Trim());
    else
      logger.LogInformation("Windows Service '{ServiceName}' stopped.", sn);

    // Poll until STOPPED before deleting — deleting a STOP_PENDING service returns error 1072.
    await WaitForServiceStoppedAsync(sn, transport, logger, CancellationToken.None).ConfigureAwait(false);

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
  /// Stops the sidecar log-watcher process for the given service annotation (best-effort).
  /// Called on both startup cleanup and shutdown so the sidecar's process registry is
  /// always consistent, even when the sidecar keeps running between AppHost sessions.
  /// </summary>
  private static async Task EnsureLogWatcherStoppedAsync(
    WindowsServiceAnnotation annotation,
    IRemoteHostTransport transport,
    ILogger logger)
  {
    if (transport.SidecarChannel is not GrpcChannel channel)
      return;

    try
    {
      var client = new SidecarService.SidecarServiceClient(channel);
      using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
      await client.StopProcessAsync(
        new StopProcessRequest { Name = annotation.LogWatcherProcessName },
        cancellationToken: cts.Token).ConfigureAwait(false);
      logger.LogDebug("Stopped stale log-watcher '{Name}' on sidecar.", annotation.LogWatcherProcessName);
    }
    catch (Exception ex) when (ex is RpcException or OperationCanceledException)
    {
      // Non-fatal: process was not running or sidecar unavailable.
      logger.LogDebug(ex, "Could not stop log-watcher '{Name}' on sidecar (non-fatal).", annotation.LogWatcherProcessName);
    }
  }

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
      new StreamLogsRequest { Name = annotation.LogWatcherProcessName, ReplayCached = true },
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
  /// the assembly name (the source registered by .NET's <c>EventLogLoggerProvider</c>)
  /// and writes them to stdout so the sidecar can capture them.
  /// </summary>
  private static async Task UploadLogWatcherScriptAsync(
    WindowsServiceAnnotation annotation,
    string assemblyName,
    string remotePath,
    IRemoteHostTransport transport,
    CancellationToken cancellationToken)
  {
    // Track the last-seen EventRecordId rather than using StartTime: Get-WinEvent's
    // StartTime filter has only second-level precision, so TimeCreated.AddMilliseconds(1)
    // does not reliably advance the cursor and the same event can be re-emitted each poll.
    // RecordId is a monotonically-increasing integer that uniquely identifies each event.
    var source = assemblyName;
    var script = new StringBuilder();
    script.AppendLine("# Auto-generated EventLog watcher — do not edit.");
    script.AppendLine($"$source = '{EscapePsString(source)}'");
    script.AppendLine("$lastId = [long]0");
    script.AppendLine("$after  = (Get-Date).AddSeconds(-10)");
    script.AppendLine($"Write-Output \"EventLog watcher active: monitoring source '$source' from $after (local)\"");
    script.AppendLine("while ($true) {");
    script.AppendLine("  try {");
    script.AppendLine("    $events = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = $source; StartTime = $after } -ErrorAction SilentlyContinue");
    script.AppendLine("    if ($events) {");
    script.AppendLine("      $events | Sort-Object RecordId | Where-Object { $_.RecordId -gt $lastId } | ForEach-Object {");
    script.AppendLine("        $lastId = $_.RecordId");
    script.AppendLine("        Write-Output \"[$($_.LevelDisplayName)] $($_.Message)\"");
    script.AppendLine("      }");
    script.AppendLine("    }");
    script.AppendLine("  } catch { }");
    script.AppendLine("  Start-Sleep -Milliseconds 500");
    script.AppendLine("}");

    var remoteScriptPath = $@"{remotePath}\watcher.ps1";
    await transport.UploadTextAsync(script.ToString(), remoteScriptPath, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Polls <c>sc.exe query</c> until the service reaches the <c>STOPPED</c> state or is
  /// no longer found. Waits up to 30 seconds before giving up.
  /// </summary>
  /// <remarks>
  /// sc.exe exit codes are NOT reliably propagated through PowerShell (the SSH shell on
  /// Windows), so we inspect the text output as well as the numeric exit code.
  /// </remarks>
  private static async Task WaitForServiceStoppedAsync(
    string serviceName,
    IRemoteHostTransport transport,
    ILogger logger,
    CancellationToken cancellationToken)
  {
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    using var linked  = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);

    while (!linked.Token.IsCancellationRequested)
    {
      var (exit, output, error) = await transport.ExecuteSshCommandAsync(
        $"sc.exe query {serviceName}", linked.Token).ConfigureAwait(false);

      if (ScServiceNotFound(exit, output, error))
        return;

      if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)
        || output.Contains("STATE : 1 ", StringComparison.OrdinalIgnoreCase))
        return;

      // Log actual sc.exe output at debug level to aid diagnosis if we get stuck.
      logger.LogDebug(
        "Waiting for Windows Service '{ServiceName}' to reach STOPPED state (exit={Exit}, output={Output})…",
        serviceName, exit, output.Trim());

      await Task.Delay(TimeSpan.FromSeconds(1), linked.Token).ConfigureAwait(false);
    }

    logger.LogWarning("Windows Service '{ServiceName}' did not reach STOPPED within the timeout.", serviceName);
  }

  /// <summary>
  /// Returns <see langword="true"/> when the <c>sc.exe query</c> response indicates the
  /// service does not exist.  Checks both the numeric exit code (when propagated) and the
  /// text output (needed when PowerShell is the SSH shell, as it always exits 0).
  /// </summary>
  private static bool ScServiceNotFound(int exit, string output, string error)
  {
    if (exit == 1060) return true;
    var combined = output + error;
    return combined.Contains("1060", StringComparison.Ordinal)
      || combined.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>Escapes a string value for safe embedding inside a PowerShell single-quoted string.</summary>
  private static string EscapePsString(string value) => value.Replace("'", "''");
}
