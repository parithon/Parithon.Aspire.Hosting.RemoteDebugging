using System.Runtime.InteropServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteDebugging.RemoteHost;
using Aspire.Hosting.RemoteDebugging.RemoteHost.Annotations;
using Aspire.Hosting.RemoteDebugging.RemoteHost.HealthChecks;
using Aspire.Hosting.RemoteDebugging.Sidecar;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;

internal sealed class SshTransport : IRemoteHostTransport
{
  private const int SidecarPort = 5055;
  private const int SidecarReadyTimeoutSeconds = 30;

  // 0 = active, 1 = terminal (disconnecting or disconnected).
  // Interlocked.CompareExchange ensures only one terminal-event path wins.
  private int _disconnected;

  private ConnectionInfo? _connectionInfo;
  private SshClient? _client;
  private SftpClient? _sftpClient;
  private RemoteHostResource? _resource;
  private string? _shell;
  private SshCommand? _vsdbgCommand;
  private IAsyncResult? _vsdbgAsyncResult;
  private DateTime _vsdbgStartTime;

  private ForwardedPortLocal? _sidecarPortForward;
  private SshCommand? _sidecarCommand;
  private CancellationTokenSource? _sidecarOutputCts;
  private GrpcChannel? _sidecarChannel;

  public event EventHandler? ConnectionDropped;
  public event EventHandler? RemoteDebuggerExited;

  public GrpcChannel? SidecarChannel => _sidecarChannel;

  public async Task ConnectAsync(RemoteHostResource resource, ILogger logger, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(resource);

    // Reset the terminal-state gate so events fire again after a reconnect.
    Interlocked.Exchange(ref _disconnected, 0);

    _resource = resource;

    var dns = resource.DnsParameter is not null
      ? await resource.DnsParameter.Resource.GetValueAsync(cancellationToken).ConfigureAwait(false)
      : resource.Dns;
    
    int port = resource.PortParameter is not null
      ? (int.TryParse(await resource.PortParameter.Resource.GetValueAsync(cancellationToken).ConfigureAwait(false), out var parsedPort) ? parsedPort : 22)
      : resource.Port ?? 22;
    
    var username = resource.Credential.UserNameParameter is not null
      ? await resource.Credential.UserNameParameter.Resource.GetValueAsync(cancellationToken).ConfigureAwait(false)
      : resource.Credential.UserName;

    var password = resource.Credential.Password is not null
      ? await resource.Credential.Password.Resource.GetValueAsync(cancellationToken).ConfigureAwait(false)
      : null;

    if (string.IsNullOrWhiteSpace(username))
      throw new InvalidOperationException("SSH username is required.");
    if (string.IsNullOrWhiteSpace(password))
      throw new InvalidOperationException("SSH password is required.");

    var authMethod = new PasswordAuthenticationMethod(username, password);
    var connectionInfo = new ConnectionInfo(dns ?? resource.Name, port, username, authMethod);
    _connectionInfo = connectionInfo;

    if (logger.IsEnabled(LogLevel.Trace))
    {
      logger.LogTrace("Opening SSH connection to {Dns}:{Port}", dns, port);
    }

    _client = new SshClient(connectionInfo);
    _client.HostKeyReceived += (s, e) =>
    {
      if (logger.IsEnabled(LogLevel.Trace))
      {
        logger.LogTrace("SSH host key received: {HostKeyName} SHA256:{Fingerprint}", e.HostKeyName, e.FingerPrintSHA256);
      }
    };
    _client.ServerIdentificationReceived += (s, e) =>
    {
      if (logger.IsEnabled(LogLevel.Trace))
      {
        logger.LogTrace("SSH Identification: {SoftwareVersion} {ProtocolVersion} {Comments}", e.SshIdentification.SoftwareVersion, e.SshIdentification.ProtocolVersion, e.SshIdentification.Comments);
      }
    };
    _client.ErrorOccurred += (s, e) =>
    {
      logger.LogError(e.Exception, "An error occurred within the SSH client.");
      // Only raise ConnectionDropped once, and only when not intentionally disconnecting.
      if (Interlocked.CompareExchange(ref _disconnected, 1, 0) == 0)
        ConnectionDropped?.Invoke(this, EventArgs.Empty);
    };
    await _client.ConnectAsync(cancellationToken);

    using var probecmd = _client.CreateCommand(@"reg query ""HKLM\SOFTWARE\OpenSSH"" /v DefaultShell");
    await Task.Factory.FromAsync(probecmd.BeginExecute(), cmd => probecmd.EndExecute(cmd));
    
    if (probecmd.ExitStatus != 0 || string.IsNullOrWhiteSpace(probecmd.Result))
    {
      if (logger.IsEnabled(LogLevel.Debug))
      {
        logger.LogDebug("OpenSSH DefaultShell registry key not found ({Error}), defaulting to cmd.exe", probecmd.Error?.Trim());
      }
      _shell = "cmd.exe";
    }
    else
    {
      var line = probecmd.Result
        .Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault(l => l.TrimStart().StartsWith("DefaultShell", StringComparison.OrdinalIgnoreCase));

      if (line is not null)
      {
        var tokens = line.Split([' ','\t'], StringSplitOptions.RemoveEmptyEntries);
        var shellValue = tokens.Length > 0 ? tokens[^1] : null;
        
        if (!string.IsNullOrWhiteSpace(shellValue))
        {
          // Extract just the filename from the registry value (may contain Windows backslashes)
          _shell = shellValue.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "cmd.exe";
          
          if (logger.IsEnabled(LogLevel.Debug))
          {
            logger.LogDebug("Detected shell from registry: '{RegistryValue}' -> '{ShellName}'", shellValue, _shell);
          }
        }
        else
        {
          _shell = "cmd.exe";
        }
      }
      else
      {
        _shell = "cmd.exe";
      }

      if (string.IsNullOrWhiteSpace(_shell) || _shell.Equals("null", StringComparison.OrdinalIgnoreCase))
        _shell = "cmd.exe";
    }

    // Connect the SFTP client with the same credentials.
    _sftpClient = new SftpClient(connectionInfo);
    await _sftpClient.ConnectAsync(cancellationToken).ConfigureAwait(false);

    // Ensure the deployment root exists on the remote host.
    if (resource.DeploymentPath is { Length: > 0 } deploymentPath)
    {
      var sftpDeploymentPath = ToSftpPath(deploymentPath);
      if (!_sftpClient.Exists(sftpDeploymentPath))
      {
        logger.LogDebug("Creating remote deployment path: {DeploymentPath}", sftpDeploymentPath);
        _sftpClient.CreateDirectory(sftpDeploymentPath);
      }
    }

    if (logger.IsEnabled(LogLevel.Information))
    {
      logger.LogInformation("SSH connection to {Dns}:{Port} verified with shell: {Shell}", dns, port, _shell);
    }
  }

  public async Task DisconnectAsync(ILogger logger, CancellationToken cancellationToken)
  {
    // Mark as terminal before disconnecting so the ErrorOccurred / BeginExecute
    // callbacks do not raise unexpected-exit events.
    Interlocked.Exchange(ref _disconnected, 1);

    // Gracefully tell the sidecar to stop all managed processes and shut down.
    // Best-effort: give it 10 seconds, then fall back to direct SSH command cancellation.
    // Use a standalone timeout CTS — do NOT link to cancellationToken because the host's
    // lifetime token is already cancelled by the time aspire stop fires this path.
    if (_sidecarChannel is not null)
    {
      try
      {
        using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = new SidecarService.SidecarServiceClient(_sidecarChannel);
        await client.ShutdownAsync(new ShutdownRequest(), cancellationToken: shutdownCts.Token)
          .ConfigureAwait(false);
        logger.LogDebug("Sidecar shutdown RPC completed successfully.");
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Sidecar shutdown RPC failed; the SSH command will be cancelled.");
      }
      finally
      {
        // Cancel the stdout pump, then close the SSH channel. StopApplication() inside the
        // RPC is async — the dotnet process may still be alive when the RPC returns. Closing
        // the SSH channel sends SIGHUP to the remote process, ensuring it is actually killed.
        _sidecarOutputCts?.Cancel();
        _sidecarCommand?.CancelAsync();
      }
    }
    else
    {
      _sidecarOutputCts?.Cancel();
      _sidecarCommand?.CancelAsync();
    }

    // TODO: Close the vsdbg agent
    _sftpClient?.Disconnect();
    _client?.Disconnect();
  }

  public async Task<RemoteDebuggerInstallationResult> InstallRemoteDebugger(ILogger logger, CancellationToken cancellationToken)
  {
    if (_client is null)
    {
      return new(false, new InvalidOperationException("The SSH client is null."));
    }
    if (_resource is null)
    {
      return new(false, new InvalidOperationException("The remote host resource is null."));
    }

    if (_resource.TryGetLastAnnotation<RemoteHostOSPlatformAnnotation>(out var platformAnnotation) && platformAnnotation is not null)
    {
      var debuggerPath = GetRemoteToolsPath(platformAnnotation.Platform);
      var pwshInstallCommand = $"iwr -Uri https://aka.ms/getvsdbgps1 -OutFile $env:TEMP\\getvsdbg.ps1; & $env:TEMP\\getvsdbg.ps1 -Version latest -InstallPath {debuggerPath}";
      var installCommand = platformAnnotation.Platform switch
      {
          var p when p == OSPlatform.Windows => _shell switch
          {
              "powershell.exe" => pwshInstallCommand,
              "pwsh.exe" => pwshInstallCommand,
              "cmd.exe" => $"powershell.exe -NonInteractive -Command {pwshInstallCommand}",
              _ => throw new InvalidOperationException($"Unsupported shell: '{_shell}'. Expected 'powershell.exe', 'pwsh.exe', or 'cmd.exe'.")
          },
          var p when p == OSPlatform.Linux =>
              $"curl -sSL https://aka.ms/getvsdbgsh | bash /dev/stdin -v latest -l {debuggerPath}",
          var p => throw new PlatformNotSupportedException($"Platform '{p}' is not supported for vsdbg installation.")
      };
      
      if (logger.IsEnabled(LogLevel.Information))
      {
        logger.LogInformation("Installing vsdbg...");
      }

      var (shell, args) = GetShell(platformAnnotation.Platform);
      using var cmd = _client.CreateCommand($"{shell} {args} \"{installCommand}\"");
      await Task.Factory.FromAsync(cmd.BeginExecute(), cmd.EndExecute);

      if (cmd.ExitStatus != 0)
      {
        if (logger.IsEnabled(LogLevel.Debug))
        {
          logger.LogDebug("Could not install the vsdbg. {Error}", cmd.Error);
        }
        return new(false, new SshException("An error occurred while installing the vsdbg"));
      }
    }
    else
    {
      return new(false, new InvalidOperationException("Could not find the platform annotation."));
    }

    if (logger.IsEnabled(LogLevel.Information))
    {
      logger.LogInformation("...completed install.");
    }

    return new(true);
  }

  public async Task<bool> StartRemoteDebugger(ILogger logger, CancellationToken cancellationToken)
  {
    if (_client is null)
    {
      return false;
    }
    if (_resource is null)
    {
      return false;
    }

    if (_resource.TryGetLastAnnotation<RemoteHostOSPlatformAnnotation>(out var platformAnnotation) && platformAnnotation is not null)
    {
      var debuggerPath = GetRemoteToolsPath(platformAnnotation.Platform);
      var startCommand = platformAnnotation.Platform switch
      {
        var p when p == OSPlatform.Windows => _shell switch {
          "powershell.exe" => $"& \"{debuggerPath}\\vsdbg.exe\" --interpreter=vscode",
          "pwsh.exe"       => $"& \"{debuggerPath}\\vsdbg.exe\" --interpreter=vscode",
          "cmd.exe"        => $"{debuggerPath}\\vsdbg.exe --interpreter=vscode",
          _ => throw new InvalidOperationException()
        },
        var p when p == OSPlatform.Linux => $"{debuggerPath}/vsdbg --interpreter=vscode",
        var p => throw new PlatformNotSupportedException($"Platform '{p}' is not supported for vsdbg.")
      };

      var (shell, args) = GetShell(platformAnnotation.Platform);
      _vsdbgCommand = _client.CreateCommand($"{shell} {args} \"{startCommand}\"");
      _vsdbgStartTime = DateTime.UtcNow;

      // Capture the command instance so the callback reads the correct object
      // even if _vsdbgCommand is replaced by a future restart.
      var capturedCommand = _vsdbgCommand;
      _vsdbgAsyncResult = capturedCommand.BeginExecute(ar =>
      {
        try {
          capturedCommand.EndExecute(ar);
          if (capturedCommand.ExitStatus != 0)
            logger.LogWarning("vsdbg exited with code {ExitCode}. {Error}", capturedCommand.ExitStatus, capturedCommand.Error);
        }
        catch (Exception ex)
        {
          logger.LogError(ex, "vsdbg exited unexpectedly.");
        }

        // Only raise the event when the exit is unexpected (not during intentional disconnect).
        // CompareExchange: if _disconnected is still 0, set it to 0 and get 0 back — fire event.
        // We do NOT set _disconnected=1 here; that is only set for connection-level termination.
        // We use a simple read so a vsdbg restart keeps _disconnected=0 for future exits.
        if (Interlocked.CompareExchange(ref _disconnected, 0, 0) == 0)
          RemoteDebuggerExited?.Invoke(this, EventArgs.Empty);
      }, null);

      return true;
    }

    return false;
  }

  public Task<SidecarDeploymentStatus> SidecarDeployedAsync(RemoteHostResource resource, CancellationToken cancellationToken)
  {
    if (_sftpClient is null || !_sftpClient.IsConnected)
      throw new InvalidOperationException("SFTP client is not connected.");

    var remoteDll = ToSftpPath($"{resource.DeploymentPath}/sidecar/aspire-sidecar.dll");

    if (!_sftpClient.Exists(remoteDll))
      return Task.FromResult(SidecarDeploymentStatus.NotDeployed);

    // Compare timestamps to detect a newer local build. If the local DLL is missing for
    // some reason (unusual in a packaged scenario), assume the remote copy is still valid.
    var localDll = Path.Combine(AppContext.BaseDirectory, "sidecar", "aspire-sidecar.dll");
    if (!File.Exists(localDll))
      return Task.FromResult(SidecarDeploymentStatus.UpToDate);

    var localTime = File.GetLastWriteTimeUtc(localDll);
    var remoteTime = _sftpClient.Get(remoteDll).LastWriteTimeUtc;

    // Allow a 2-second tolerance to absorb filesystem timestamp precision differences
    // (e.g. FAT32 has 2-second granularity; some SFTP servers truncate to seconds).
    var upToDate = Math.Abs((localTime - remoteTime).TotalSeconds) <= 2;
    return Task.FromResult(upToDate ? SidecarDeploymentStatus.UpToDate : SidecarDeploymentStatus.Outdated);
  }

  public async Task ShutdownRunningSidecarAsync(RemoteHostResource resource, ILogger logger, CancellationToken cancellationToken)
  {
    if (_client is null)
      throw new InvalidOperationException("SSH client is not connected.");

    // Use a temporary port forward so we can reach the (possibly) running sidecar
    // without interfering with the permanent forward set up later by StartSidecarAsync.
    var tempForward = new ForwardedPortLocal("127.0.0.1", 0, "127.0.0.1", SidecarPort);
    _client.AddForwardedPort(tempForward);
    tempForward.Start();

    try
    {
      using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{tempForward.BoundPort}");
      var client = new SidecarService.SidecarServiceClient(channel);

      // Quick ping — if the sidecar isn't running, there's nothing to shut down.
      try
      {
        await client.PingAsync(
          new PingRequest(),
          deadline: DateTime.UtcNow.AddSeconds(3),
          cancellationToken: cancellationToken).ConfigureAwait(false);
      }
      catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
      {
        logger.LogDebug("Outdated sidecar is not running — no graceful shutdown needed.");
        return;
      }

      logger.LogInformation("Outdated sidecar is running; sending Shutdown before redeploying.");
      try
      {
        using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        shutdownCts.CancelAfter(TimeSpan.FromSeconds(10));
        await client.ShutdownAsync(new ShutdownRequest(), cancellationToken: shutdownCts.Token)
          .ConfigureAwait(false);
        logger.LogDebug("Sidecar shutdown RPC completed; waiting for process to exit.");
        // Brief pause to let StopApplication() finish before the SSH kill below.
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Graceful shutdown RPC failed; falling back to SSH kill.");
        await KillSidecarProcessAsync(resource, logger, cancellationToken).ConfigureAwait(false);
      }
    }
    finally
    {
      tempForward.Stop();
      _client.RemoveForwardedPort(tempForward);
      tempForward.Dispose();
    }
  }

  /// <summary>
  /// SSH kill as a last resort when the graceful gRPC shutdown fails.
  /// Sends SIGTERM to any process whose command line contains "aspire-sidecar.dll".
  /// </summary>
  private async Task KillSidecarProcessAsync(RemoteHostResource resource, ILogger logger, CancellationToken cancellationToken)
  {
    if (!resource.TryGetLastAnnotation<RemoteHostOSPlatformAnnotation>(out var platformAnnotation))
      return;

    var killCmd = platformAnnotation!.Platform == OSPlatform.Windows
      ? "taskkill /F /IM aspire-sidecar.exe 2>nul & exit /B 0"
      : "pkill -f 'aspire-sidecar.dll' 2>/dev/null; true";

    var cmd = _client!.CreateCommand(killCmd);
    await Task.Factory.FromAsync(cmd.BeginExecute(), cmd.EndExecute).ConfigureAwait(false);
    logger.LogDebug("SSH kill command sent (exit {Code}).", cmd.ExitStatus);

    // Give the OS a moment to release the file handles before the redeploy cleans the directory.
    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
  }

  public async Task StartSidecarAsync(RemoteHostResource resource, ILogger logger, bool isReconnect, CancellationToken cancellationToken)
  {
    if (_client is null)
      throw new InvalidOperationException("SSH client is not connected.");

    if (!resource.TryGetLastAnnotation<RemoteHostOSPlatformAnnotation>(out var platformAnnotation) || platformAnnotation is null)
      throw new InvalidOperationException("Cannot start sidecar: no OS platform annotation found on the remote host resource.");

    // Forward a local OS-assigned port through the SSH tunnel to the sidecar's gRPC port.
    _sidecarPortForward = new ForwardedPortLocal("127.0.0.1", 0, "127.0.0.1", SidecarPort);
    _client.AddForwardedPort(_sidecarPortForward);
    _sidecarPortForward.Start();
    var localPort = _sidecarPortForward.BoundPort;

    if (logger.IsEnabled(LogLevel.Debug))
      logger.LogDebug("SSH port forward established: localhost:{LocalPort} → remote:{SidecarPort}", localPort, SidecarPort);

    // Create the gRPC channel over the SSH tunnel (used whether we reconnect or start fresh).
    _sidecarChannel = GrpcChannel.ForAddress($"http://127.0.0.1:{localPort}");
    var client = new SidecarService.SidecarServiceClient(_sidecarChannel);

    // Check if the sidecar is already running from a previous session.
    // Use a short deadline — we don't want to stall if it's simply not running yet.
    PingResponse? pingResponse = null;
    try
    {
      pingResponse = await client.PingAsync(
        new PingRequest(),
        deadline: DateTime.UtcNow.AddSeconds(3),
        cancellationToken: cancellationToken).ConfigureAwait(false);

      logger.LogInformation(
        "Sidecar already running (version: {Version}, {Count} process(es)).",
        pingResponse.Version, pingResponse.ActiveProcessCount);
    }
    catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
    {
      // Not running — will start below.
    }

    if (pingResponse is not null)
    {
      if (isReconnect)
      {
        // SSH dropped and was re-established. The sidecar and its managed processes are still
        // running. Do NOT reset — callers will re-subscribe to StreamLogs(replay_cached: true)
        // to retrieve any output produced while the AppHost was offline.
        logger.LogInformation(
          "Reconnected to existing sidecar ({Count} process(es) still running).",
          pingResponse.ActiveProcessCount);
      }
      else
      {
        // Fresh AppHost session. Stop any processes left over from the previous session.
        var resetResponse = await client.ResetAsync(new ResetRequest(), cancellationToken: cancellationToken)
          .ConfigureAwait(false);
        logger.LogInformation(
          "Sidecar reset: {Stopped} previous process(es) stopped.",
          resetResponse.ProcessesStopped);
      }
      return;
    }

    // Sidecar is not running — start it now.
    var sidecarDll = $"{resource.DeploymentPath}/sidecar/aspire-sidecar.dll";
    var (shell, shellArgs) = GetShell(platformAnnotation.Platform);
    _sidecarCommand = _client.CreateCommand($"{shell} {shellArgs} \"dotnet {sidecarDll}\"");

    var capturedCommand = _sidecarCommand;
    capturedCommand.BeginExecute(ar =>
    {
      try { capturedCommand.EndExecute(ar); }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Sidecar process exited unexpectedly.");
      }

      if (Interlocked.CompareExchange(ref _disconnected, 0, 0) == 0)
        logger.LogWarning("Sidecar process has stopped on the remote host.");
    }, null);

    // Pump stdout from the remote sidecar process into the AppHost dashboard log.
    // The CTS lets DisconnectAsync cancel the pump after the SSH command is cancelled.
    _sidecarOutputCts = new CancellationTokenSource();
    _ = PumpSidecarOutputAsync(capturedCommand, logger, _sidecarOutputCts.Token);

    logger.LogInformation("Sidecar process started; waiting for gRPC readiness on localhost:{LocalPort}", localPort);

    // Retry Ping until sidecar is ready or the timeout elapses.
    var deadline = DateTime.UtcNow.AddSeconds(SidecarReadyTimeoutSeconds);
    while (DateTime.UtcNow < deadline)
    {
      cancellationToken.ThrowIfCancellationRequested();
      try
      {
        var response = await client.PingAsync(
          new PingRequest(),
          deadline: DateTime.UtcNow.AddSeconds(3),
          cancellationToken: cancellationToken).ConfigureAwait(false);

        logger.LogInformation("Sidecar ready (version: {Version})", response.Version);
        return;
      }
      catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
      {
        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
      }
    }

    throw new TimeoutException(
      $"Sidecar did not become ready within {SidecarReadyTimeoutSeconds} seconds on localhost:{localPort}.");
  }

  /// <summary>
  /// Reads stdout from the remote sidecar SSH command and forwards each line to the
  /// dashboard logger, mapping Serilog level tags ([INF]/[WRN]/[ERR]/[FTL]) to the
  /// corresponding <see cref="ILogger"/> severity so lines appear correctly coloured
  /// in the Aspire dashboard.
  /// </summary>
  private static async Task PumpSidecarOutputAsync(SshCommand command, ILogger logger, CancellationToken cancellationToken)
  {
    try
    {
      using var reader = new StreamReader(command.OutputStream);
      while (!cancellationToken.IsCancellationRequested)
      {
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null)
          break; // stream closed — sidecar process ended

        // Map the Serilog 3-char level tag to the ILogger level so the dashboard
        // colours warnings/errors appropriately.
        if (line.Contains("[ERR]") || line.Contains("[FTL]"))
          logger.LogError("{Line}", line);
        else if (line.Contains("[WRN]"))
          logger.LogWarning("{Line}", line);
        else
          logger.LogInformation("{Line}", line);
      }
    }
    catch (OperationCanceledException)
    {
      // Normal cancellation on disconnect — nothing to report.
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Sidecar stdout pump stopped unexpectedly.");
    }
  }

  public async Task<ResourceHealthCheckResult> CheckSidecarHealthAsync(ILogger logger, CancellationToken cancellationToken)
  {
    if (_client is null || !_client.IsConnected)
      return ResourceHealthCheckResult.Unhealthy("SSH connection is not established.");

    if (_sidecarChannel is null)
      return ResourceHealthCheckResult.Unknown("Sidecar gRPC channel is not yet established.");

    try
    {
      var client = new SidecarService.SidecarServiceClient(_sidecarChannel);
      var response = await client.PingAsync(
        new PingRequest(),
        deadline: DateTime.UtcNow.AddSeconds(5),
        cancellationToken: cancellationToken).ConfigureAwait(false);

      return ResourceHealthCheckResult.Healthy(
        $"Sidecar healthy ({response.ActiveProcessCount} process(es) running).");
    }
    catch (RpcException ex)
    {
      return ResourceHealthCheckResult.Unhealthy($"Sidecar ping failed: {ex.Status.Detail}");
    }
  }

  public Task<ResourceHealthCheckResult> CheckVsdbgHealthAsync(ILogger logger, CancellationToken cancellationToken)
  {
    if (_vsdbgCommand is null || _vsdbgAsyncResult is null)
      return Task.FromResult(ResourceHealthCheckResult.Unknown("vsdbg has not been started yet."));

    if (_vsdbgAsyncResult.IsCompleted)
      return Task.FromResult(ResourceHealthCheckResult.Unhealthy("vsdbg has exited."));

    var uptime = DateTime.UtcNow - _vsdbgStartTime;
    return Task.FromResult(ResourceHealthCheckResult.Healthy($"vsdbg running for {uptime.TotalSeconds:F0}s."));
  }

  public void Dispose()
  {
    _sidecarChannel?.Dispose();
    _sidecarChannel = null;
    if (_sidecarPortForward is not null)
    {
      _sidecarPortForward.Stop();
      _sidecarPortForward.Dispose();
      _sidecarPortForward = null;
    }
    _sftpClient?.Dispose();
    _sftpClient = null;
    _client?.Dispose();
    _client = null;
  }

  public async Task DeployDirectoryAsync(string localDirectory, string remoteDirectory, ILogger logger, CancellationToken cancellationToken)
  {
    if (_sftpClient is null || !_sftpClient.IsConnected)
      throw new InvalidOperationException("SFTP client is not connected.");

    var sftpRemoteDirectory = ToSftpPath(remoteDirectory);

    // Abort any in-flight SFTP call when the token fires by disconnecting the client.
    using var cancelReg = cancellationToken.Register(() =>
    {
      try { _sftpClient.Disconnect(); } catch { /* best-effort */ }
    });

    // Clean the remote directory to prevent stale artifacts from a previous deploy.
    if (_sftpClient.Exists(sftpRemoteDirectory))
    {
      logger.LogDebug("Cleaning remote directory: {RemoteDir}", sftpRemoteDirectory);
      await DeleteRemoteDirectoryAsync(_sftpClient, sftpRemoteDirectory, cancellationToken).ConfigureAwait(false);
    }

    var localRoot = new DirectoryInfo(localDirectory);
    await UploadDirectoryAsync(_sftpClient, localRoot, sftpRemoteDirectory, logger, cancellationToken).ConfigureAwait(false);

    logger.LogInformation("Deployed {LocalDir} → {RemoteDir}", localDirectory, sftpRemoteDirectory);
  }

  private static async Task UploadDirectoryAsync(SftpClient sftp, DirectoryInfo localDir, string remotePath, ILogger logger, CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    sftp.CreateDirectory(remotePath);

    foreach (var file in localDir.EnumerateFiles())
    {
      cancellationToken.ThrowIfCancellationRequested();
      var remotFile = $"{remotePath}/{file.Name}";
      logger.LogDebug("Uploading {File}", remotFile);
      await using var stream = file.OpenRead();
      await sftp.UploadFileAsync(stream, remotFile, cancellationToken).ConfigureAwait(false);
    }

    foreach (var subDir in localDir.EnumerateDirectories())
    {
      await UploadDirectoryAsync(sftp, subDir, $"{remotePath}/{subDir.Name}", logger, cancellationToken).ConfigureAwait(false);
    }
  }

  private static async Task DeleteRemoteDirectoryAsync(SftpClient sftp, string remotePath, CancellationToken cancellationToken)
  {
    foreach (var entry in sftp.ListDirectory(remotePath).Where(e => e.Name is not ("." or "..")))
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (entry.IsDirectory)
        await DeleteRemoteDirectoryAsync(sftp, entry.FullName, cancellationToken).ConfigureAwait(false);
      else
        sftp.DeleteFile(entry.FullName);
    }
    sftp.DeleteDirectory(remotePath);
  }

  /// <summary>
  /// Runs a remote <c>echo</c> via SSH to expand env-vars/tilde in the tools path
  /// so the result can be used directly with SFTP (which does no shell expansion).
  /// </summary>
  private Task<string> ResolveRemoteToolsPathAsync(OSPlatform platform, CancellationToken cancellationToken)
    => ResolveRemotePathAsync(GetRemoteToolsPath(platform), platform, cancellationToken);

  /// <summary>
  /// Runs <c>echo <paramref name="shellExpr"/></c> on the remote shell and returns the
  /// expanded absolute path. Use this to resolve any shell expression (env-vars, tilde)
  /// before embedding it in SFTP calls or command strings.
  /// </summary>
  private async Task<string> ResolveRemotePathAsync(string shellExpr, OSPlatform platform, CancellationToken cancellationToken)
  {
    if (_client is null)
      throw new InvalidOperationException("SSH client is not connected.");

    var (shell, shellArgs) = GetShell(platform);
    using var cmd = _client.CreateCommand($"{shell} {shellArgs} \"echo {shellExpr}\"");
    await Task.Factory.FromAsync(cmd.BeginExecute(), cmd.EndExecute).ConfigureAwait(false);

    var resolved = cmd.Result?.Trim();
    if (string.IsNullOrEmpty(resolved))
      throw new InvalidOperationException($"Could not resolve path expression '{shellExpr}' (echo returned empty).");

    return resolved;
  }

  /// <summary>
  /// Returns the remote tools path: the configured <see cref="RemoteHostResource.RemoteToolsPath"/>
  /// if set, otherwise the platform-appropriate default (shell-aware on Windows).
  /// </summary>
  private string GetRemoteToolsPath(OSPlatform platform)
  {
    if (_resource?.RemoteToolsPath is { Length: > 0 } customPath)
      return customPath;

    if (platform == OSPlatform.Windows)
      return _shell switch
      {
        "powershell.exe" or "pwsh.exe" => "$env:LOCALAPPDATA\\Microsoft\\vsdbg",
        _ => "%LOCALAPPDATA%\\Microsoft\\vsdbg"
      };

    if (platform == OSPlatform.Linux)
      return "~/.vsdbg";

    throw new PlatformNotSupportedException($"Platform '{platform}' is not supported.");
  }

  private (string shell, string shellArgs) GetShell(OSPlatform platform)  {
    var shell = _shell ?? "cmd.exe";
    if (platform == OSPlatform.Windows)
      return shell switch
        {
          "cmd.exe" => ("cmd.exe", "/c"),
          "powershell.exe" => ("powershell.exe", "-NonInteractive -Command"),
          "pwsh.exe" => ("pwsh.exe", "-NonInteractive -Command"),
          _ => throw new NotSupportedException()
        };
    if (platform == OSPlatform.Linux)
      return ("/bin/bash", "-c");

    throw new PlatformNotSupportedException($"Platform '{platform}' is not supported.");
  }

  /// <summary>
  /// Converts a path to an SFTP-compatible forward-slash path.
  /// Windows drive-letter paths (e.g. <c>C:\Temp</c>) are mapped to
  /// <c>/C:/Temp</c> so the SFTP protocol can locate them correctly.
  /// </summary>
  private static string ToSftpPath(string path)
  {
    // Windows absolute path: e.g. C:\Temp or C:/Temp → /C:/Temp
    if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
      return '/' + path.Replace('\\', '/');

    return path.Replace('\\', '/');
  }
}
