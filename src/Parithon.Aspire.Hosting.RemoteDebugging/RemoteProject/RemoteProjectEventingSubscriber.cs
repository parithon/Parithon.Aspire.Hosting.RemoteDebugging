using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost.HealthChecks;
using Microsoft.Extensions.Logging;
using Aspire.Hosting;

namespace Parithon.Aspire.Hosting.RemoteDebugging.RemoteProject;

internal sealed class RemoteProjectEventingSubscriber<TProject>(ResourceNotificationService notifications, ResourceLoggerService loggers) : IDistributedApplicationEventingSubscriber where TProject : IProjectMetadata
{
  public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
  {
    eventing.Subscribe<AfterResourcesCreatedEvent>((@event, ct) =>
    {
      var resources = @event.Model.Resources
        .OfType<RemoteProjectResource<TProject>>();

      foreach (var resource in resources)
      {
        _ = Task.Run(async () =>
        {
          try
          {
            string? previousParentState = null;

            await foreach (var update in notifications.WatchAsync(ct))
            {
              if (update.Resource.Name != resource.Parent.Name) continue;

              var state = update.Snapshot.State?.Text;

              // Only act on state transitions, not repeated snapshots of the same state.
              if (state == previousParentState) continue;
              previousParentState = state;

              if (state == KnownResourceStates.Running)
              {
                var runToken = resource.CreateRunToken(ct);
                _ = Task.Run(async () =>
                {
                  try
                  {
                    await RemoteProjectRunner.RunAsync(resource, notifications, loggers, runToken);
                  }
                  catch (OperationCanceledException) { }
                  catch (Exception ex)
                  {
                    var logger = loggers.GetLogger(resource);
                    logger.LogError(ex, "A failure occurred while running the resource {Name}", resource.Name);
                  }
                }, CancellationToken.None);
              }
              else if (state == KnownResourceStates.Waiting
                             || state == KnownResourceStates.Stopping
                             || state == KnownResourceStates.NotStarted
                             || state == KnownResourceStates.FailedToStart
                             || state == KnownResourceStates.Exited)
              {
                resource.CancelRun();
                await notifications.PublishUpdateAsync(resource, s => s with
                {
                  State = new ResourceStateSnapshot(KnownResourceStates.Waiting, null)
                }).ConfigureAwait(false);
              }
            }
          }
          catch (OperationCanceledException) { }
          catch (Exception ex)
          {
            var logger = loggers.GetLogger(resource);
            logger.LogError(ex, "Host state watch failed for resource {Name}", resource.Name);
          }
        }, ct);
      }

      return Task.CompletedTask;
    });
    return Task.CompletedTask;
  }
}
