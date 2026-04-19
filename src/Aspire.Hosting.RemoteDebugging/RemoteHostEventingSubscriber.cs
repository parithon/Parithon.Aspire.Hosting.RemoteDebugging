using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;

namespace Aspire.Hosting.RemoteDebugging;

internal sealed class RemoteHostEventingSubscriber(ResourceNotificationService notifications, ResourceLoggerService loggers) : IDistributedApplicationEventingSubscriber
{
  public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
  {
    eventing.Subscribe<AfterResourcesCreatedEvent>((@event, ct) =>
    {
      var resources = @event.Model.Resources
        .OfType<RemoteHostResource>()
        .Where(r => !r.HasAnnotationOfType<ExplicitStartupAnnotation>())
        .Where(r => !r.HasAnnotationOfType<RemoteHostTransportAnnotation>());

      foreach (var resource in resources)
      {
        _ = RemoteHostConnector.ConnectAsync(resource, notifications, loggers, ct);
      }

      return Task.CompletedTask;
    });

    eventing.Subscribe<ResourceStoppedEvent>(async (@event, ct) =>
    {
      if (@event.Resource is not RemoteHostResource remoteHost)
        return;

      if (!remoteHost.TryGetLastAnnotation<RemoteHostTransportAnnotation>(out var annotation))
        return;

      var logger = loggers.GetLogger(remoteHost);
      await RemoteHostConnector.DisconnectAsync(remoteHost, notifications, loggers, ct);
      annotation.Dispose();
    });

    return Task.CompletedTask;
  }
}
