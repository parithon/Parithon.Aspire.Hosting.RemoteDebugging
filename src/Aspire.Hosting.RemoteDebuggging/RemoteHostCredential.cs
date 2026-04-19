using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.RemoteDebuggging;

public sealed class RemoteHostCredential
{
  public RemoteHostCredential(string username, IResourceBuilder<ParameterResource> password)
  {
    UserName = username;
    Password = password;
  }

  public RemoteHostCredential(IResourceBuilder<ParameterResource> username, IResourceBuilder<ParameterResource> password)
  {
    UserNameParameter = username;
    Password = password;
  }

  public string? UserName { get; init; }
  public IResourceBuilder<ParameterResource>? UserNameParameter { get; init; }
  public IResourceBuilder<ParameterResource>? Password { get; init; }
}
