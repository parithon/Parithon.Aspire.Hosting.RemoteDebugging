using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.RemoteDebugging.RemoteHost;
using Aspire.Hosting.RemoteDebugging.RemoteProject;

namespace Aspire.Hosting;

public static class RemoteProjectResourceExtensions
{
  public static IResourceBuilder<RemoteProjectResource<TProject>> AddRemoteProject<TProject>(this IDistributedApplicationBuilder builder, [ResourceName] string name, IResourceBuilder<RemoteHostResource> host) where TProject : IProjectMetadata
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentNullException.ThrowIfNull(name);

    builder.Services.TryAddEventingSubscriber<RemoteProjectEventingSubscriber<TProject>>();

    var resource = new RemoteProjectResource<TProject>(name, host.Resource);

    return builder.AddResource(resource)
      .WithInitialState(new CustomResourceSnapshot
      {
        ResourceType = "RemoteProject",
        State = KnownResourceStates.Waiting,
        CreationTimeStamp = DateTime.UtcNow,
        Properties = []
      });
  }
}
