using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.RemoteDebugging.RemoteHost;
using Aspire.Hosting.RemoteDebugging.RemoteHost.Annotations;
using Aspire.Hosting.RemoteDebugging.RemoteProject;
using Aspire.Hosting.RemoteDebugging.RemoteProject.Annotations;
using Aspire.Hosting.RemoteDebugging.RemoteProject.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.InteropServices;

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
        var runToken = resource.CreateRunToken(context.CancellationToken);
        _ = Task.Run(async () =>
        {
          try { await RemoteProjectRunner.RunAsync(resource, notifications, loggers, runToken); }
          catch (OperationCanceledException) { }
        }, CancellationToken.None);
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
      .WithCommand(name: "stop", displayName: "Stop", async context =>
      {
        var notifications = context.ServiceProvider.GetRequiredService<ResourceNotificationService>();
        var loggers = context.ServiceProvider.GetRequiredService<ResourceLoggerService>();
        await RemoteProjectRunner.StopAsync(resource, notifications, loggers, context.CancellationToken);
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

  /// <summary>
  /// Adds an environment variable that will be injected into the remote process when it starts.
  /// </summary>
  public static IResourceBuilder<RemoteProjectResource<TProject>> WithEnvironment<TProject>(
    this IResourceBuilder<RemoteProjectResource<TProject>> builder,
    string key,
    string value) where TProject : IProjectMetadata
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentException.ThrowIfNullOrWhiteSpace(key);
    ArgumentNullException.ThrowIfNull(value);

    builder.Resource.EnvironmentVariables[key] = value;
    return builder;
  }

  /// <summary>
  /// Configures the remote project to run as an ephemeral Windows Service on the remote host.
  /// The service is installed when the AppHost starts and removed when the AppHost stops (or on
  /// the next connect if the AppHost crashed without cleaning up).
  /// </summary>
  /// <param name="builder">The remote project resource builder.</param>
  /// <param name="serviceName">
  /// Optional SCM service name. Defaults to the resource name with spaces replaced by hyphens.
  /// Service names must not contain spaces.
  /// </param>
  /// <param name="displayName">
  /// Optional display name shown in the Windows Services management console.
  /// Defaults to the resource name.
  /// </param>
  /// <param name="description">Optional service description.</param>
  /// <exception cref="InvalidOperationException">
  /// Thrown if the parent <see cref="RemoteHostResource"/> is not configured for Windows.
  /// </exception>
  public static IResourceBuilder<RemoteProjectResource<TProject>> AsWindowsService<TProject>(
    this IResourceBuilder<RemoteProjectResource<TProject>> builder,
    string? serviceName = null,
    string? displayName = null,
    string? description = null) where TProject : IProjectMetadata
  {
    ArgumentNullException.ThrowIfNull(builder);

    var resource = builder.Resource;
    var host     = resource.Parent;

    // Validate that the remote host is Windows.
    if (host.TryGetLastAnnotation<RemoteHostOSPlatformAnnotation>(out var platformAnnotation)
      && platformAnnotation is not null
      && platformAnnotation.Platform != OSPlatform.Windows)
    {
      throw new InvalidOperationException(
        $"AsWindowsService() requires the remote host '{host.Name}' to be configured for Windows, " +
        $"but it is configured for '{platformAnnotation.Platform}'.");
    }

    // Build a safe SCM service name: no spaces, max 256 chars.
    var resolvedServiceName = serviceName
      ?? resource.Name.Replace(' ', '-').Replace('_', '-');

    resource.Annotations.Add(new WindowsServiceAnnotation(resolvedServiceName)
    {
      DisplayName = displayName,
      Description = description,
    });

    return builder;
  }

  /// <summary>
  /// Enables log file tailing for a Windows Service resource.
  /// A PowerShell <c>Get-Content -Wait</c> tailer runs on the sidecar and streams each line
  /// to the Aspire console. Error and Fatal lines are routed to stderr so they appear as
  /// errors in the dashboard.
  /// </summary>
  /// <param name="builder">The remote project resource builder.</param>
  /// <param name="logFilePath">
  /// Absolute path to the log file on the remote host (e.g. <c>C:\Windows\Logs\app\app.log</c>).
  /// The directory is created automatically by the application if it does not exist.
  /// </param>
  /// <param name="outputTemplate">
  /// Optional Serilog <c>outputTemplate</c> used to derive the error-level detection pattern.
  /// Supports <c>{Level:u3}</c>, <c>{Level:u4}</c>, <c>{Level:w}</c>, and plain <c>{Level}</c>.
  /// Defaults to a conservative pattern matching all common error-level representations.
  /// </param>
  /// <exception cref="InvalidOperationException">
  /// Thrown if <see cref="AsWindowsService"/> was not called first on this resource.
  /// </exception>
  public static IResourceBuilder<RemoteProjectResource<TProject>> WithLoggingSupport<TProject>(
    this IResourceBuilder<RemoteProjectResource<TProject>> builder,
    string logFilePath,
    string? outputTemplate = null) where TProject : IProjectMetadata
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);

    if (!builder.Resource.TryGetLastAnnotation<WindowsServiceAnnotation>(out _))
      throw new InvalidOperationException(
        "WithLoggingSupport() requires AsWindowsService() to be called first on this resource.");

    builder.Resource.Annotations.Add(new LoggingSupportAnnotation(logFilePath)
    {
      OutputTemplate = outputTemplate,
    });

    return builder;
  }
}

