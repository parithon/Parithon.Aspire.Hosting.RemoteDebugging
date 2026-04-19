using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;

namespace Aspire.Hosting.RemoteDebuggging;

internal sealed class RemoteHostEventingSubscriber(ResourceNotificationService notifications, ResourceLoggerService loggers) : IDistributedApplicationEventingSubscriber
{
  public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
  {
    eventing.Subscribe<AfterResourcesCreatedEvent>(async (@event, ct) =>
    {
      var tasks = @event.Model.Resources
        .OfType<RemoteHostResource>()
        .Where(r => !r.HasAnnotationOfType<ExplicitStartupAnnotation>())
        .Select(r => RemoteHostConnector.ConnectAsync(r, notifications, loggers, ct));

      await Task.WhenAll(tasks).ConfigureAwait(false);
    });

    return Task.CompletedTask;
  }
}
