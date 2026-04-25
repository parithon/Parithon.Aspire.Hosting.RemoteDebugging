using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;
using Aspire.Hosting.RemoteDebugging.RemoteProject.Annotations;
using Google.Protobuf.Collections;
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
///   <item><see cref="StartAndStreamAsync"/> — starts the service and blocks until cancelled.</item>
///   <item><see cref="StopAndUninstallAsync"/> — stops the service and removes it.</item>
/// </list>
/// <para>
/// Console log capture is not supported for Windows Services — services have no stdout/stderr.
/// </para>
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
    var envScript = new StringBuilder();
    envScript.AppendLine($@"$regPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\{sn}'");
    if (environment.Count > 0)
    {
      envScript.AppendLine("$values  = @(");
      foreach (var kv in environment)
        envScript.AppendLine($"  '{kv.Key}={EscapePsString(kv.Value)}'");
      envScript.AppendLine(")");
      envScript.AppendLine("Set-ItemProperty -Path $regPath -Name 'Environment' -Value $values -Type MultiString");
    }
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
      logger.LogDebug("Configured service '{ServiceName}' ({Count} env var(s)).", sn, environment.Count);

    logger.LogInformation("Windows Service '{ServiceName}' installed at '{BinPath}'.", sn, binPath);
  }

  /// <summary>
  /// Starts the Windows Service and blocks until <paramref name="cancellationToken"/> is
  /// cancelled (i.e. until the AppHost stops the resource).
  /// </summary>
  /// <remarks>
  /// Console log capture is not supported for Windows Services — services have no
  /// stdout/stderr pipe. Logs remain available in the Windows Event Viewer on the remote host.
  /// </remarks>
  public static async Task StartAndStreamAsync<TProject>(
    RemoteProjectResource<TProject> resource,
    WindowsServiceAnnotation annotation,
    IRemoteHostTransport transport,
    ILogger logger,
    CancellationToken cancellationToken) where TProject : IProjectMetadata
  {
    var sn = annotation.ServiceName;

    // Start the SCM service.
    var (startExit, _, startErr) = await transport.ExecuteSshCommandAsync(
      $"sc.exe start {sn}", cancellationToken).ConfigureAwait(false);
    if (startExit != 0)
      throw new InvalidOperationException($"Failed to start Windows Service '{sn}' (exit {startExit}): {startErr.Trim()}");

    logger.LogInformation("Windows Service '{ServiceName}' started successfully. Console log capture is not supported for Windows Services.", sn);

    // Hold until the AppHost stops (cancellation token fires).
    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
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
