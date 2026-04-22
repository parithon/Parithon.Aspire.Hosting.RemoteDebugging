using System.Runtime.InteropServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteDebugging.RemoteHost;
using Aspire.Hosting.RemoteDebugging.RemoteHost.Annotations;
using Aspire.Hosting.RemoteDebugging.RemoteHost.HealthChecks;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;

internal sealed class SshTransport : IRemoteHostTransport
{
  // 0 = active, 1 = terminal (disconnecting or disconnected).
  // Interlocked.CompareExchange ensures only one terminal-event path wins.
  private int _disconnected;

  private SshClient? _client;
  private RemoteHostResource? _resource;
  private string? _shell;
  private SshCommand? _vsdbgCommand;
  private IAsyncResult? _vsdbgAsyncResult;
  private DateTime _vsdbgStartTime;

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
      var pwshInstallCommand = "iwr -Uri https://aka.ms/getvsdbgps1 -OutFile $env:TEMP\\getvsdbg.ps1; & $env:TEMP\\getvsdbg.ps1 -Version latest -InstallPath $env:LOCALAPPDATA\\Microsoft\\vsdbg";
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
              "curl -sSL https://aka.ms/getvsdbgsh | bash /dev/stdin -v latest -l ~/.vsdbg",
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
      var startCommand = platformAnnotation.Platform switch
      {
        var p when p == OSPlatform.Windows => _shell switch {
          "powershell.exe" => "& \"$env:LOCALAPPDATA\\Microsoft\\vsdbg\\vsdbg.exe\" --interpreter=vscode",
          "cmd.exe" => "%LOCALAPPDATA%\\Microsoft\\vsdbg\\vsdbg.exe --interpreter=vscode",
          _ => throw new InvalidOperationException()
        },
        var p when p == OSPlatform.Linux => "~/.vsdbg/vsdbg --interpreter=vscode",
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
    _client?.Dispose();
    _client = null;
  }

  private (string shell, string shellArgs) GetShell(OSPlatform platform)
  {
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
}
