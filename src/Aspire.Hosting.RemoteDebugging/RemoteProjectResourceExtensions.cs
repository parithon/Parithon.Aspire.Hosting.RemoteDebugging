using System.Net.Mime;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.RemoteDebugging.RemoteHost;
using Aspire.Hosting.RemoteDebugging.RemoteProject;
using Aspire.Hosting.RemoteDebugging.RemoteProject.HealthChecks;
using Microsoft.Extensions.DependencyInjection;

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
      })
      .WithCommand(name: "start", displayName: "Start", async context =>
      {
        var notifications = context.ServiceProvider.GetRequiredService<ResourceNotificationService>();
        var loggers = context.ServiceProvider.GetRequiredService<ResourceLoggerService>();        
        // TODO: Start the application on the remote host
        return CommandResults.Success();
      }, new CommandOptions
      {
        UpdateState = ctx =>
        {
          var state = ctx.ResourceSnapshot.State?.Text;
          return KnownRemoteProjectStates.IsRunning(state ?? string.Empty)
            ? ResourceCommandState.Disabled
            : ResourceCommandState.Enabled; 
        },
        IconName = "Play",
        IconVariant = IconVariant.Filled,
        IsHighlighted = true
      })
      .WithCommand(name: "stop", displayName:" Stop", async context =>
      {
        var notifications = context.ServiceProvider.GetRequiredService<ResourceNotificationService>();
        var loggers = context.ServiceProvider.GetRequiredService<ResourceLoggerService>();        
        // TODO: Stop the application on the remote host
        return CommandResults.Success();
      }, new CommandOptions
      {
        UpdateState = ctx =>
        {
          var state = ctx.ResourceSnapshot.State?.Text;
          return !KnownRemoteProjectStates.IsRunning(state ?? string.Empty)
            ? ResourceCommandState.Disabled
            : ResourceCommandState.Enabled;
        },
        IconName = "Stop",
        IconVariant = IconVariant.Filled,
        IsHighlighted = true
      });
  }
}
