using System.Runtime.InteropServices;
using Aspire.Hosting.ApplicationModel;
using Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;

namespace Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost;

public sealed class RemoteHostResource(string name)
  : Resource(name), IResourceWithEnvironment, IResourceWithEndpoints, IResourceWithWaitSupport, IComputeResource
{
  public const string TYPE = "RemoteHost";
  public required RemoteHostCredential Credential { get; set; }
  public TransportType TransportType { get; set; } = TransportType.SSH;
  public OSPlatform Platform { get; set; }
  public string Dns {get; set; } = name;
  public int? Port { get; set; }

  /// <summary>
  /// The path on the remote host where tools (vsdbg) are installed and run from.
  /// When <see langword="null"/>, a platform-appropriate default is used:
  /// Windows → <c>%LOCALAPPDATA%\Microsoft\vsdbg</c>, Linux → <c>~/.vsdbg</c>.
  /// </summary>
  public string? RemoteToolsPath { get; set; }

  /// <summary>
  /// The root path on the remote host where project binaries are deployed.
  /// Defaults to <c>/tmp</c>, which exists on Linux and is created automatically on Windows
  /// during host initialization. Override with an absolute path or use
  /// <c>.WithDeploymentPath(...)</c> to change it.
  /// </summary>
  public string DeploymentPath { get; set; } = "/tmp";

  /// <summary>
  /// Expected SHA-256 fingerprint of the remote host's SSH public key (without the "SHA256:" prefix).
  /// When set, the SSH connection is rejected if the received fingerprint does not match.
  /// When <see langword="null"/>, host key verification is skipped and a warning is emitted.
  /// Configure with <c>.WithHostKeyFingerprint(...)</c>.
  /// </summary>
  public string? HostKeyFingerprint { get; set; }

  /// <summary>
  /// The version of vsdbg to install on the remote host. Defaults to <c>"latest"</c>.
  /// Pin to a specific version (e.g. <c>"17.13.30618.01"</c>) to avoid supply-chain risk
  /// from fetching an unverified latest build. Configure with <c>.WithVsdbgVersion(...)</c>.
  /// </summary>
  public string VsdbgVersion { get; set; } = "latest";

  internal IResourceBuilder<ParameterResource>? DnsParameter { get; set; }
  internal IResourceBuilder<ParameterResource>? PortParameter { get; set; }

  /// <summary>Serializes concurrent connect/disconnect operations for this resource.</summary>
  internal SemaphoreSlim ConnectGate { get; } = new SemaphoreSlim(1, 1);
}
