using System.Runtime.InteropServices;
using Aspire.Hosting.ApplicationModel;

using Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;

namespace Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost;

public sealed class RemoteHostOptions
{
  public OSPlatform Platform { get; set; }
  public RemoteHostCredential? Credential { get; set; }
  public TransportType? TransportType { get; set; }
  internal string? Dns { get; set; }
  internal IResourceBuilder<ParameterResource>? DnsParameter { get; set; }
  internal int? Port { get; set; }
  internal IResourceBuilder<ParameterResource>? PortParameter { get; set; }

  /// <summary>
  /// The path on the remote host where tools (vsdbg) are installed and run from.
  /// When <see langword="null"/>, a platform-appropriate default is used:
  /// Windows → <c>%LOCALAPPDATA%\Microsoft\vsdbg</c>, Linux → <c>~/.vsdbg</c>.
  /// </summary>
  public string? RemoteToolsPath { get; set; }

  /// <summary>
  /// The root path on the remote host where project binaries are deployed.
  /// When <see langword="null"/>, a platform-appropriate default is used:
  /// Windows → <c>%USERPROFILE%\.aspire\deployments</c>, Linux → <c>~/.aspire/deployments</c>.
  /// </summary>
  public string? DeploymentPath { get; set; }

  public void SetDns(string dns)
  {
    Dns = dns;
  }
  public void SetDns(IResourceBuilder<ParameterResource> dns)
  {
    DnsParameter = dns;
  }

  public void SetPort(int port)
  {
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(port, 0, nameof(port));
    ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535, nameof(port));
    Port = port;
  }

  public void SetPort(IResourceBuilder<ParameterResource> port)
  {
    PortParameter = port;
  }
}
