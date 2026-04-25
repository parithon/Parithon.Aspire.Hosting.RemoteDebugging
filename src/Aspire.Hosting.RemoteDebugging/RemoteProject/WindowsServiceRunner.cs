using System.Text;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;
using Aspire.Hosting.RemoteDebugging.RemoteProject.Annotations;
using Aspire.Hosting.RemoteDebugging.Sidecar;
using Google.Protobuf.Collections;
using Grpc.Core;
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

      // Still stop any stale log tailer from a previous session (best-effort).
      if (resource.TryGetLastAnnotation<LoggingSupportAnnotation>(out _))
        await EnsureLogTailerStoppedAsync(TailerProcessName(sn), transport, logger).ConfigureAwait(false);

      return;
    }

    logger.LogWarning(
      "Stale Windows Service '{ServiceName}' found from a previous session. Stopping and removing it.",
      sn);

    // Stop stale log tailer before stopping the service (best-effort).
    if (resource.TryGetLastAnnotation<LoggingSupportAnnotation>(out _))
      await EnsureLogTailerStoppedAsync(TailerProcessName(sn), transport, logger).ConfigureAwait(false);

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

    // Upload the log tailer script if logging support is configured.
    if (resource.TryGetLastAnnotation<LoggingSupportAnnotation>(out var logAnnotation) && logAnnotation is not null)
    {
      var tailerScript     = BuildTailerScript(logAnnotation.LogFilePath, logAnnotation.OutputTemplate);
      var tailerScriptPath = $@"{remotePath}\log-tailer.ps1";
      await transport.UploadTextAsync(tailerScript, tailerScriptPath, cancellationToken).ConfigureAwait(false);
      logger.LogDebug("Log tailer script uploaded to '{Path}'.", tailerScriptPath);
    }
  }

  /// <summary>
  /// Starts the Windows Service and either streams log output (when
  /// <see cref="LoggingSupportAnnotation"/> is present) or holds until
  /// <paramref name="cancellationToken"/> is cancelled.
  /// </summary>
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

    logger.LogInformation("Windows Service '{ServiceName}' started.", sn);

    // If logging support is configured, start the log tailer and stream its output.
    if (resource.TryGetLastAnnotation<LoggingSupportAnnotation>(out _)
      && resource.RemoteDeploymentPath is string remotePath)
    {
      await StreamLogTailerAsync(sn, remotePath, transport, logger, cancellationToken).ConfigureAwait(false);
    }
    else
    {
      logger.LogInformation(
        "Console log capture is not configured for Windows Service '{ServiceName}'. " +
        "Use WithLoggingSupport() to enable log file tailing.",
        sn);
      await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>
  /// Stops the Windows Service (and log tailer if configured), then removes (uninstalls) the service.
  /// </summary>
  public static Task StopAndUninstallAsync<TProject>(
    RemoteProjectResource<TProject> resource,
    WindowsServiceAnnotation annotation,
    IRemoteHostTransport transport,
    ILogger logger,
    CancellationToken cancellationToken) where TProject : IProjectMetadata
  {
    var tailerName = resource.TryGetLastAnnotation<LoggingSupportAnnotation>(out _)
      ? TailerProcessName(annotation.ServiceName)
      : null;
    return StopAndUninstallAsync(annotation, transport, logger, cancellationToken, tailerName);
  }

  /// <summary>
  /// Stops the Windows Service (and log tailer if <paramref name="tailerProcessName"/> is
  /// provided), then removes (uninstalls) the service. Called by
  /// <see cref="RemoteHostShutdownService"/> during AppHost shutdown where no typed
  /// <see cref="RemoteProjectResource{TProject}"/> is available.
  /// </summary>
  internal static async Task StopAndUninstallAsync(
    WindowsServiceAnnotation annotation,
    IRemoteHostTransport transport,
    ILogger logger,
    CancellationToken cancellationToken,
    string? tailerProcessName = null)
  {
    var sn = annotation.ServiceName;

    // Stop the log tailer before stopping the service (best-effort).
    if (tailerProcessName is not null)
      await EnsureLogTailerStoppedAsync(tailerProcessName, transport, logger).ConfigureAwait(false);

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

  // ── Log tailer helpers ────────────────────────────────────────────────────

  /// <summary>Returns the sidecar process name for the log tailer of a given service.</summary>
  private static string TailerProcessName(string serviceName) => $"{serviceName}-log-tailer";

  /// <summary>
  /// Sends a <c>StopProcess</c> RPC to the sidecar for the log tailer process.
  /// No-op if the sidecar channel is unavailable; logs a debug message and returns.
  /// </summary>
  private static async Task EnsureLogTailerStoppedAsync(
    string tailerProcessName,
    IRemoteHostTransport transport,
    ILogger logger)
  {
    var channel = transport.SidecarChannel;
    if (channel is null)
    {
      logger.LogDebug(
        "Sidecar channel not available; skipping log tailer stop for '{Name}'.", tailerProcessName);
      return;
    }

    var client = new SidecarService.SidecarServiceClient(channel);
    try
    {
      using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
      await client.StopProcessAsync(
        new StopProcessRequest { Name = tailerProcessName },
        cancellationToken: cts.Token).ConfigureAwait(false);
      logger.LogDebug("Log tailer '{Name}' stopped.", tailerProcessName);
    }
    catch (Exception ex) when (ex is RpcException or OperationCanceledException)
    {
      logger.LogDebug(ex, "Could not stop log tailer '{Name}' — it may have already exited.", tailerProcessName);
    }
  }

  /// <summary>
  /// Starts a PowerShell log tailer via the sidecar and streams its output to the Aspire console.
  /// Blocks until <paramref name="cancellationToken"/> is cancelled.
  /// </summary>
  private static async Task StreamLogTailerAsync(
    string serviceName,
    string remotePath,
    IRemoteHostTransport transport,
    ILogger logger,
    CancellationToken cancellationToken)
  {
    var channel = transport.SidecarChannel;
    if (channel is null)
    {
      logger.LogWarning(
        "Sidecar channel not available; log tailer cannot start for '{ServiceName}'.", serviceName);
      await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
      return;
    }

    var client         = new SidecarService.SidecarServiceClient(channel);
    var tailerName     = TailerProcessName(serviceName);
    var tailerScript   = $@"{remotePath}\log-tailer.ps1";
    var tailerArgs     = $@"-NonInteractive -ExecutionPolicy Bypass -File ""{tailerScript}""";

    var request = new StartProcessRequest
    {
      Name             = tailerName,
      WorkingDirectory = remotePath,
      Executable       = "powershell.exe",
      EntryPoint       = tailerArgs,
    };

    try
    {
      var response = await client.StartProcessAsync(request, cancellationToken: cancellationToken)
        .ConfigureAwait(false);
      logger.LogInformation(
        "Log tailer for service '{ServiceName}' started (PID {Pid}).", serviceName, response.Pid);
    }
    catch (RpcException ex)
    {
      logger.LogError(
        ex, "Failed to start log tailer for '{ServiceName}': {Detail}", serviceName, ex.Status.Detail);
      await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
      return;
    }

    // Stream tailer output to the Aspire console until cancelled.
    using var call = client.StreamLogs(
      new StreamLogsRequest { Name = tailerName, ReplayCached = false },
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
      logger.LogWarning("Log tailer process '{Name}' not found on sidecar.", tailerName);
    }
  }

  /// <summary>
  /// Generates the PowerShell log-tailer script that tails <paramref name="logFilePath"/>
  /// and writes error/fatal lines to stderr (so the sidecar marks them <c>is_error = true</c>).
  /// </summary>
  private static string BuildTailerScript(string logFilePath, string? outputTemplate)
  {
    var errorPattern = DeriveLevelErrorPattern(outputTemplate);
    var escapedPath  = EscapePsString(logFilePath);

    var sb = new StringBuilder();
    sb.AppendLine($"$logFile      = '{escapedPath}'");
    sb.AppendLine($"$errorPattern = '{errorPattern}'");
    sb.AppendLine();
    sb.AppendLine("Write-Output \"Log tailer: waiting for '$logFile'...\"");
    sb.AppendLine("while (-not (Test-Path $logFile)) { Start-Sleep -Milliseconds 500 }");
    sb.AppendLine("Write-Output \"Log tailer: tailing '$logFile'\"");
    sb.AppendLine();
    sb.AppendLine("Get-Content -Path $logFile -Wait -Tail 0 | ForEach-Object {");
    sb.AppendLine("    if ($_ -match $errorPattern) {");
    sb.AppendLine("        [Console]::Error.WriteLine($_)");
    sb.AppendLine("    } else {");
    sb.AppendLine("        Write-Output $_");
    sb.AppendLine("    }");
    sb.AppendLine("}");
    return sb.ToString();
  }

  /// <summary>
  /// Derives a PowerShell <c>-match</c> regular-expression pattern for error/fatal lines from
  /// a Serilog <c>outputTemplate</c>.  Inspects the <c>{Level:…}</c> format specifier.
  /// </summary>
  internal static string DeriveLevelErrorPattern(string? outputTemplate)
  {
    if (outputTemplate is null)
      return @"(ERR|FTL|ERRO|FATL|Error|Fatal|ERROR|FATAL)";

    var match = Regex.Match(outputTemplate, @"\{Level(?::([^}]+))?\}", RegexOptions.IgnoreCase);
    if (!match.Success)
      return @"(ERR|FTL|ERRO|FATL|Error|Fatal|ERROR|FATAL)";

    return match.Groups[1].Value.ToLowerInvariant() switch
    {
      "u3" => @"\b(ERR|FTL)\b",
      "u4" => @"\b(ERRO|FATL)\b",
      "w"  => @"\b(error|fatal)\b",
      _    => @"\b(Error|Fatal)\b",
    };
  }
}
