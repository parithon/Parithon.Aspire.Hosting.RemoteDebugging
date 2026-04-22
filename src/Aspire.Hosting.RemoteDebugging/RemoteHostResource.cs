using System.Runtime.InteropServices;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.RemoteDebugging;

public sealed class RemoteHostResource(string name)
  : Resource(name), IResourceWithEnvironment, IResourceWithEndpoints, IResourceWithWaitSupport, IComputeResource
{
  public const string TYPE = "RemoteHost";
  public required RemoteHostCredential Credential { get; set; }
  public TransportType TransportType { get; set; } = TransportType.SSH;
  public OSPlatform Platform { get; set; }
  public string Dns {get; set; } = name;
  public int? Port { get; set; }
  internal IResourceBuilder<ParameterResource>? DnsParameter { get; set; }
  internal IResourceBuilder<ParameterResource>? PortParameter { get; set; }

  /// <summary>Serializes concurrent connect/disconnect operations for this resource.</summary>
  internal SemaphoreSlim ConnectGate { get; } = new SemaphoreSlim(1, 1);
}
