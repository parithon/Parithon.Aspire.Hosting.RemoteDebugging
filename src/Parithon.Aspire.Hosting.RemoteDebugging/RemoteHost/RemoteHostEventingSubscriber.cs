using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Aspire.Hosting;

namespace Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost;

internal sealed class RemoteHostEventingSubscriber(ResourceNotificationService notifications, ResourceLoggerService loggers, IServiceProvider services) : IDistributedApplicationEventingSubscriber
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
        _ = Task.Run(async () =>
        {
          try
          {
            await RemoteHostConnector.ConnectAsync(resource, notifications, loggers, services, ct).ConfigureAwait(false);
          }
          catch (OperationCanceledException)
          {
            // Expected when shutting down
          }
          catch (Exception ex)
          {
            var logger = loggers.GetLogger(resource);
            logger.LogError(ex, "Failed to auto-connect to remote host {Name}", resource.Name);
          }
        }, ct);
      }

      return Task.CompletedTask;
    });

    return Task.CompletedTask;
  }
}

