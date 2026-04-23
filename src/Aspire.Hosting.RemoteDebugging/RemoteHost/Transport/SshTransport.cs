using System.Runtime.InteropServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteDebugging.RemoteHost;
using Aspire.Hosting.RemoteDebugging.RemoteHost.Annotations;
using Aspire.Hosting.RemoteDebugging.RemoteHost.HealthChecks;
using Aspire.Hosting.RemoteDebugging.Sidecar.Proto;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;

internal sealed class SshTransport : IRemoteHostTransport
{
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
  // SSH port-forward that tunnels gRPC: local 127.0.0.1:5055 → remote 127.0.0.1:5055
  private ForwardedPortLocal? _sidecarPortForward;
  private const int SidecarPort = 5055;

  public event EventHandler? ConnectionDropped;
  public event EventHandler? RemoteDebuggerExited;

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

    // Detect the remote RID via `dotnet --info` unless the user supplied an override.
    if (string.IsNullOrWhiteSpace(resource.RuntimeIdentifier))
    {
      try
      {
        using var ridCmd = _client.CreateCommand("dotnet --info");
        await Task.Factory.FromAsync(ridCmd.BeginExecute(), ridCmd.EndExecute);

        var ridLine = ridCmd.Result
          .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
          .FirstOrDefault(l => l.TrimStart().StartsWith("RID:", StringComparison.OrdinalIgnoreCase));

        if (ridLine is not null)
        {
          var rid = ridLine.Split(':', 2)[1].Trim();
          if (!string.IsNullOrWhiteSpace(rid))
          {
            resource.DetectedRuntimeIdentifier = rid;
            if (logger.IsEnabled(LogLevel.Debug))
              logger.LogDebug("Detected remote RID: {RID}", rid);
          }
        }
        else
        {
          logger.LogWarning("Could not parse RID from 'dotnet --info' output. Rebuild decisions will default to always rebuild.");
        }
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "RID detection via 'dotnet --info' failed. Rebuild decisions will default to always rebuild.");
      }
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
    // TODO: Close the vsdbg agent
    await Task.Delay(1, cancellationToken);
    _sftpClient?.Disconnect();
    _client?.Disconnect();
    return;
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

  public Task<ResourceHealthCheckResult> CheckHealthAsync(ILogger logger, CancellationToken cancellationToken)
  {
    if (_client is null || !_client.IsConnected)
      return Task.FromResult(ResourceHealthCheckResult.Unhealthy("SSH connection is not established"));

    if (_vsdbgCommand is null || _vsdbgAsyncResult is null)
      return Task.FromResult(ResourceHealthCheckResult.Unknown("vsdbg has not been started yet"));

    if (_vsdbgAsyncResult.IsCompleted)
      return Task.FromResult(ResourceHealthCheckResult.Unhealthy("vsdbg has exited"));

    var uptime = DateTime.UtcNow - _vsdbgStartTime;
    return Task.FromResult(ResourceHealthCheckResult.Healthy($"vsdbg running for {uptime.TotalSeconds:F0}s"));
  }

  public void Dispose()
  {
    if (_sidecarPortForward is not null)
    {
      try { _sidecarPortForward.Stop(); } catch { /* best-effort */ }
      _client?.RemoveForwardedPort(_sidecarPortForward);
      _sidecarPortForward.Dispose();
      _sidecarPortForward = null;
    }
    _sftpClient?.Dispose();
    _sftpClient = null;
    _client?.Dispose();
    _client = null;
  }

  public async Task DeploySidecarAsync(string localSidecarDir, ILogger logger, CancellationToken cancellationToken)
  {
    if (_sftpClient is null || !_sftpClient.IsConnected)
      throw new InvalidOperationException("SFTP client is not connected.");
    if (_resource is null)
      throw new InvalidOperationException("Remote host resource is null.");
    if (!_resource.TryGetLastAnnotation<RemoteHostOSPlatformAnnotation>(out var platformAnnotation) || platformAnnotation is null)
      throw new InvalidOperationException("Could not determine remote platform.");

    // GetRemoteToolsPath returns shell-expression strings (e.g. $env:LOCALAPPDATA, ~).
    // SFTP speaks the file protocol directly — no shell expansion happens.
    // Resolve the real path by running an echo command over SSH first.
    var remoteDir = ToSftpPath(await ResolveRemoteToolsPathAsync(platformAnnotation.Platform, cancellationToken).ConfigureAwait(false));

    if (!_sftpClient.Exists(remoteDir))
    {
      logger.LogDebug("Creating remote tools directory: {RemoteDir}", remoteDir);
      _sftpClient.CreateDirectory(remoteDir);
    }

    foreach (var file in Directory.EnumerateFiles(localSidecarDir))
    {
      cancellationToken.ThrowIfCancellationRequested();
      var remotePath = $"{remoteDir}/{Path.GetFileName(file)}";
      logger.LogDebug("Uploading sidecar file: {RemotePath}", remotePath);
      await using var stream = File.OpenRead(file);
      await _sftpClient.UploadFileAsync(stream, remotePath, cancellationToken).ConfigureAwait(false);
    }

    logger.LogInformation("aspire-sidecar deployed to {RemoteDir}", remoteDir);
  }

  public async Task<bool> StartSidecarDaemonAsync(ILogger logger, CancellationToken cancellationToken)
  {
    if (_client is null || _resource is null)
      return false;
    if (!_resource.TryGetLastAnnotation<RemoteHostOSPlatformAnnotation>(out var platformAnnotation) || platformAnnotation is null)
      return false;

    var toolsPath = await ResolveRemoteToolsPathAsync(platformAnnotation.Platform, cancellationToken).ConfigureAwait(false);
    var dllPath   = platformAnnotation.Platform == OSPlatform.Windows
      ? $"{toolsPath}\\aspire-sidecar.dll"
      : $"{toolsPath}/aspire-sidecar.dll";

    // Check whether the daemon is already listening on port 5055 before launching.
    string portCheckCmd;
    string startCmd;

    if (platformAnnotation.Platform == OSPlatform.Linux)
    {
      portCheckCmd = $"ss -tlnp 2>/dev/null | grep -q ':{SidecarPort}' && echo running || echo stopped";
      startCmd     = $"nohup dotnet '{dllPath}' daemon </dev/null >/dev/null 2>&1 &";
    }
    else if (platformAnnotation.Platform == OSPlatform.Windows)
    {
      // PowerShell — single-quoted paths, no shell variable expansion.
      portCheckCmd = _shell == "cmd.exe"
        ? $"netstat -ano | findstr /C:\":{SidecarPort}\" | findstr LISTENING > nul 2>&1 && echo running || echo stopped"
        : $"if(Get-NetTCPConnection -LocalPort {SidecarPort} -State Listen -ErrorAction SilentlyContinue){{'running'}}else{{'stopped'}}";
      startCmd = _shell == "cmd.exe"
        ? $"start /B dotnet \"{dllPath}\" daemon"
        : $"Start-Process -WindowStyle Hidden -FilePath dotnet -ArgumentList '{dllPath}','daemon'";
    }
    else
    {
      throw new PlatformNotSupportedException($"Platform '{platformAnnotation.Platform}' is not supported.");
    }

    var (shell, shellArgs) = GetShell(platformAnnotation.Platform);

    // If not already running, start the daemon.
    using var checkCmd = _client.CreateCommand($"{shell} {shellArgs} \"{portCheckCmd}\"");
    await Task.Factory.FromAsync(checkCmd.BeginExecute(), checkCmd.EndExecute).ConfigureAwait(false);

    if (checkCmd.Result?.Trim() != "running")
    {
      using var launchCmd = _client.CreateCommand($"{shell} {shellArgs} \"{startCmd}\"");
      await Task.Factory.FromAsync(launchCmd.BeginExecute(), launchCmd.EndExecute).ConfigureAwait(false);

      if (launchCmd.ExitStatus != 0)
      {
        logger.LogWarning("aspire-sidecar launch returned exit code {Code}. {Error}", launchCmd.ExitStatus, launchCmd.Error?.Trim());
        return false;
      }

      // Give the daemon a moment to bind its port.
      await Task.Delay(1_500, cancellationToken).ConfigureAwait(false);
    }

    // Set up (or re-use) the SSH port forward so the AppHost can reach the gRPC server.
    if (_sidecarPortForward is null)
    {
      _sidecarPortForward = new ForwardedPortLocal("127.0.0.1", SidecarPort, "127.0.0.1", SidecarPort);
      _client.AddForwardedPort(_sidecarPortForward);
    }

    if (!_sidecarPortForward.IsStarted)
      _sidecarPortForward.Start();

    logger.LogInformation("aspire-sidecar daemon started (or already running). SSH tunnel active on port {Port}.", SidecarPort);
    return true;
  }

  public async Task<ResourceHealthCheckResult> CheckSidecarHealthAsync(ILogger logger, CancellationToken cancellationToken)
  {
    if (_sidecarPortForward is null || !_sidecarPortForward.IsStarted)
      return ResourceHealthCheckResult.Unhealthy("aspire-sidecar port forward is not active");

    try
    {
      using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{SidecarPort}");
      var client = new SidecarService.SidecarServiceClient(channel);
      var deadline = DateTime.UtcNow.AddSeconds(5);
      var reply    = await client.PingAsync(new PingRequest(), deadline: deadline, cancellationToken: cancellationToken).ConfigureAwait(false);
      return ResourceHealthCheckResult.Healthy($"aspire-sidecar v{reply.Version} is running");
    }
    catch (Exception ex)
    {
      logger.LogDebug(ex, "aspire-sidecar gRPC Ping failed");
      return ResourceHealthCheckResult.Unhealthy($"aspire-sidecar gRPC Ping failed: {ex.Message}");
    }
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
