using System.Runtime.InteropServices;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.RemoteDebuggging;

public sealed class RemoteHostOptions
{
  public OSPlatform Platform { get; set; }
  public RemoteHostCredential? Credential { get; set; }
  public TransportType? TransportType { get; set; }
  internal string? Dns { get; set; }
  internal IResourceBuilder<ParameterResource>? DnsParameter { get; set; }
  internal int? Port { get; set; }
  internal IResourceBuilder<ParameterResource>? PortParameter { get; set; }

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
    ArgumentOutOfRangeException.ThrowIfLessThan(0, port);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(65536, port);
    Port = port;
  }

  public void SetPort(IResourceBuilder<ParameterResource> port)
  {
    PortParameter = port;
  }
}
