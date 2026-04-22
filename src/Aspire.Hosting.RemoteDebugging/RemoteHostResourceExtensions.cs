using System.Runtime.InteropServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteDebugging;

public static class RemoteHostResourceExtensions
{
  public static IResourceBuilder<RemoteHostResource> AddRemoteHost(this IDistributedApplicationBuilder builder, [ResourceName] string name, OSPlatform platform, RemoteHostCredential credential)
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(credential);
    
    return builder.AddRemoteHost(name, opt =>
    {
      opt.Platform = platform;
      opt.Credential = credential;
    });
  }

  public static IResourceBuilder<RemoteHostResource> AddRemoteHost(this IDistributedApplicationBuilder builder, [ResourceName] string name, Action<RemoteHostOptions> configure)
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(configure);

    builder.Services.TryAddEventingSubscriber<RemoteHostEventingSubscriber>();

    var options = new RemoteHostOptions();
    configure(options);

    if (options.Platform != OSPlatform.Windows && options.Platform != OSPlatform.Linux)
    {
      throw new PlatformNotSupportedException("Only Windows and Linux are supported platforms.");
    }

    if (options.Credential is null)
    {
      throw new ArgumentException("A credential must be provided via options.Credential.", nameof(configure));
    }

    var remoteHost = new RemoteHostResource(name)
    {
      Platform = options.Platform,
      Credential = options.Credential,
      TransportType = options.TransportType ?? TransportType.SSH,
      Dns = options.Dns ?? name,
      DnsParameter = options.DnsParameter,
      Port = options.Port,
      PortParameter = options.PortParameter
    };

    var resource = builder.AddResource(remoteHost);

    // Register the health check with Aspire's health check service so it
    // appears in the dashboard "Health checks" section.
    var healthCheckKey = $"{name}-vsdbg";
    builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
      healthCheckKey,
      sp => new RemoteHostHealthCheck(remoteHost, sp.GetRequiredService<ILoggerFactory>().CreateLogger<RemoteHostHealthCheck>()),
      failureStatus: null,
      tags: null)
    {
      Delay = TimeSpan.FromSeconds(5),
      Period = TimeSpan.FromSeconds(30)
    });

    if (options.Credential.Password is not null)
    {
      resource.WithReferenceRelationship(options.Credential.Password);
    }

    if (options.DnsParameter is not null)
    {
      resource.WithReferenceRelationship(options.DnsParameter);
    }

    if (options.PortParameter is not null)
    {
      resource.WithReferenceRelationship(options.PortParameter);
    }

    var platformAnnotation = new RemoteHostOSPlatformAnnotation(options.Platform);

    return resource
      .WithInitialState(new CustomResourceSnapshot
      {
        ResourceType = RemoteHostResource.TYPE,
        State = KnownRemoteResourceStates.DisconnectedSnapshot,
        CreationTimeStamp = DateTime.UtcNow,
        Properties = []
      })
      .WithAnnotation(platformAnnotation)
      .WithAnnotation(new HealthCheckAnnotation(healthCheckKey))
      .WithCommand(name: "connect", displayName: "Connect", executeCommand: async context =>
      {
        var notifications = context.ServiceProvider.GetRequiredService<ResourceNotificationService>();
        var loggers = context.ServiceProvider.GetRequiredService<ResourceLoggerService>();
        await RemoteHostConnector.ConnectAsync(remoteHost, notifications, loggers, context.CancellationToken);
        return CommandResults.Success();
      }, commandOptions: new CommandOptions
      {
        UpdateState = ctx =>
        {
          var state = ctx.ResourceSnapshot.State?.Text;
          return state == KnownRemoteResourceStates.Connecting || state == KnownRemoteResourceStates.Connected
            ? ResourceCommandState.Disabled
            : ResourceCommandState.Enabled;
        },
        IconName = "PlugConnected",
        IconVariant = IconVariant.Filled,
        IsHighlighted = true
      })
      .WithCommand(name: "disconnect", displayName: "Disconnect", executeCommand: async context =>
      {
        var notifications = context.ServiceProvider.GetRequiredService<ResourceNotificationService>();
        var loggers = context.ServiceProvider.GetRequiredService<ResourceLoggerService>();
        await RemoteHostConnector.DisconnectAsync(remoteHost, notifications, loggers, context.CancellationToken);
        return CommandResults.Success();
      }, commandOptions: new CommandOptions
      {
        UpdateState = ctx =>
        {
          var state = ctx.ResourceSnapshot.State?.Text;
          return state == KnownRemoteResourceStates.Connected
            ? ResourceCommandState.Enabled
            : ResourceCommandState.Disabled;
        },
        IconName = "PlugDisconnected",
        IconVariant = IconVariant.Filled,
        IsHighlighted = true
      });
  }

  public static IResourceBuilder<RemoteHostResource> AsPlatform(this IResourceBuilder<RemoteHostResource> builder, OSPlatform platform)
  {
    ArgumentNullException.ThrowIfNull(builder);

    builder.Resource.Platform = platform;

    return builder;
  }

  public static IResourceBuilder<RemoteHostResource> WithEndpoint(this IResourceBuilder<RemoteHostResource> builder, string dns)
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentNullException.ThrowIfNull(dns);

    builder.Resource.Dns = dns;
    builder.Resource.TransportType = TransportType.SSH;
    builder.Resource.Port = 22;

    return builder;
  }

  public static IResourceBuilder<RemoteHostResource> WithEndpoint(this IResourceBuilder<RemoteHostResource> builder, TransportType type, string dns)
  {
    var endpoint = WithEndpoint(builder, dns);
    endpoint.Resource.TransportType = type;
    return endpoint;
  }

  public static IResourceBuilder<RemoteHostResource> WithEndpoint(this IResourceBuilder<RemoteHostResource> builder, IResourceBuilder<ParameterResource> dns)
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentNullException.ThrowIfNull(dns);

    builder.Resource.DnsParameter = dns;
    builder.Resource.TransportType = TransportType.SSH;
    builder.Resource.Port = 22;

    builder.WithReferenceRelationship(dns);

    return builder;
  }

  public static IResourceBuilder<RemoteHostResource> WithEndpoint(this IResourceBuilder<RemoteHostResource> builder, TransportType type, IResourceBuilder<ParameterResource> dns)
  {
    var endpoint = WithEndpoint(builder, dns);
    endpoint.Resource.TransportType = type;
    return endpoint;
  }

  public static IResourceBuilder<RemoteHostResource> WithEndpoint(this IResourceBuilder<RemoteHostResource> builder, string dns, int port)
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentNullException.ThrowIfNull(dns);
    ArgumentOutOfRangeException.ThrowIfEqual(port, 0, nameof(port));
    ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535, nameof(port));

    builder.Resource.Dns = dns;
    builder.Resource.Port = port;

    return builder;
  }

  public static IResourceBuilder<RemoteHostResource> WithEndpoint(this IResourceBuilder<RemoteHostResource> builder, string dns, TransportType type, int port)
  {
    var endpoint = WithEndpoint(builder, dns, port);
    endpoint.Resource.TransportType = type;
    return endpoint;
  }

  public static IResourceBuilder<RemoteHostResource> WithEndpoint(this IResourceBuilder<RemoteHostResource> builder, IResourceBuilder<ParameterResource> dns, int port)
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentNullException.ThrowIfNull(dns);
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(port, 0, nameof(port));
    ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535, nameof(port));

    builder.Resource.DnsParameter = dns;
    builder.Resource.Port = port;

    builder.WithReferenceRelationship(dns);

    return builder;
  }

  public static IResourceBuilder<RemoteHostResource> WithEndpoint(this IResourceBuilder<RemoteHostResource> builder, IResourceBuilder<ParameterResource> dns, TransportType type, int port)
  {
    var endpoint = WithEndpoint(builder, dns, port);
    endpoint.Resource.TransportType = type;
    return endpoint;
  }

  public static IResourceBuilder<RemoteHostResource> WithEndpoint(this IResourceBuilder<RemoteHostResource> builder, string dns, IResourceBuilder<ParameterResource> port)
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentNullException.ThrowIfNull(dns);

    builder.Resource.Dns = dns;
    builder.Resource.PortParameter = port;

    builder.WithReferenceRelationship(port);

    return builder;
  }

  public static IResourceBuilder<RemoteHostResource> WithEndpoint(this IResourceBuilder<RemoteHostResource> builder, string dns, TransportType type, IResourceBuilder<ParameterResource> port)
  {
    var endpoint = WithEndpoint(builder, dns, port);
    endpoint.Resource.TransportType = type;
    return endpoint;
  }

  public static IResourceBuilder<RemoteHostResource> WithEndpoint(this IResourceBuilder<RemoteHostResource> builder, IResourceBuilder<ParameterResource> dns, IResourceBuilder<ParameterResource> port)
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentNullException.ThrowIfNull(dns);

    builder.Resource.DnsParameter = dns;
    builder.Resource.PortParameter = port;

    builder.WithReferenceRelationship(dns);
    builder.WithReferenceRelationship(port);

    return builder;
  }

  public static IResourceBuilder<RemoteHostResource> WithEndpoint(this IResourceBuilder<RemoteHostResource> builder, IResourceBuilder<ParameterResource> dns, TransportType type, IResourceBuilder<ParameterResource> port)
  {
    var endpoint = WithEndpoint(builder, dns, port);
    endpoint.Resource.TransportType = type;
    return endpoint;
  }
}
