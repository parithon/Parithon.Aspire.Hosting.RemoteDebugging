using System.Runtime.InteropServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.RemoteDebugging.RemoteHost;
using Aspire.Hosting.RemoteDebugging.RemoteHost.Annotations;
using Aspire.Hosting.RemoteDebugging.RemoteHost.HealthChecks;
using Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

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
    builder.Services.AddHostedService<RemoteHostShutdownService>();

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
      PortParameter = options.PortParameter,
      RemoteToolsPath = options.RemoteToolsPath,
      DeploymentPath = options.DeploymentPath ?? "/tmp"
    };

    var resource = builder.AddResource(remoteHost);

    // Register two health checks so each component is visible separately in the dashboard.
    var sidecarHealthCheckKey = $"{name}-sidecar";
    builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
      sidecarHealthCheckKey,
      sp => new RemoteHostHealthCheck(remoteHost, sp.GetRequiredService<ILoggerFactory>().CreateLogger<RemoteHostHealthCheck>()),
      failureStatus: null,
      tags: null)
    {
      Delay = TimeSpan.FromSeconds(5),
      Period = TimeSpan.FromSeconds(30)
    });

    var vsdbgHealthCheckKey = $"{name}-vsdbg";
    builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
      vsdbgHealthCheckKey,
      sp => new VsdbgHealthCheck(remoteHost, sp.GetRequiredService<ILoggerFactory>().CreateLogger<VsdbgHealthCheck>()),
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
      .WithAnnotation(new HealthCheckAnnotation(sidecarHealthCheckKey))
      .WithAnnotation(new HealthCheckAnnotation(vsdbgHealthCheckKey))
      .WithCommand(name: "connect", displayName: "Connect", executeCommand: async context =>
      {
        var notifications = context.ServiceProvider.GetRequiredService<ResourceNotificationService>();
        var loggers = context.ServiceProvider.GetRequiredService<ResourceLoggerService>();
        await RemoteHostConnector.ConnectAsync(remoteHost, notifications, loggers, context.ServiceProvider, context.CancellationToken);
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
        await RemoteHostConnector.DisconnectAsync(remoteHost, notifications, loggers, context.CancellationToken, sendShutdown: true);
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

  /// <summary>
  /// Sets the path on the remote host where tools (vsdbg) are installed and run from.
  /// </summary>
  public static IResourceBuilder<RemoteHostResource> WithRemoteToolsPath(this IResourceBuilder<RemoteHostResource> builder, string path)
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentException.ThrowIfNullOrWhiteSpace(path);

    builder.Resource.RemoteToolsPath = path;
    return builder;
  }

  /// <summary>
  /// Sets the root path on the remote host where project binaries are deployed.
  /// </summary>
  public static IResourceBuilder<RemoteHostResource> WithDeploymentPath(this IResourceBuilder<RemoteHostResource> builder, string path)
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentException.ThrowIfNullOrWhiteSpace(path);

    // Validate path: must start with / (Unix) or be a drive letter (Windows).
    // No path traversal (..), no spaces, no shell metacharacters.
    // Allow: alphanumerics, hyphens, underscores, dots (version), forward slashes.
    if (!System.Text.RegularExpressions.Regex.IsMatch(path, @"^[a-zA-Z0-9._/-]+$"))
    {
      throw new ArgumentException(
        $"Deployment path contains invalid characters: '{path}'. " +
        $"Only alphanumerics, hyphens, underscores, dots, and forward slashes are allowed.",
        nameof(path));
    }

    // Reject path traversal attempts.
    if (path.Contains("..") || path.EndsWith('/') == false && !path.StartsWith("/") && !System.Text.RegularExpressions.Regex.IsMatch(path, @"^[A-Z]:"))
    {
      throw new ArgumentException(
        $"Deployment path is invalid: '{path}'. Must be absolute (starting with / on Unix or drive letter on Windows).",
        nameof(path));
    }

    builder.Resource.DeploymentPath = path;
    return builder;
  }

  /// <summary>
  /// Pins the expected SHA-256 fingerprint of the remote host's SSH public key.
  /// The connection is rejected if the received fingerprint does not match.
  /// </summary>
  /// <param name="builder">The remote host resource builder.</param>
  /// <param name="sha256Fingerprint">
  /// The expected SHA-256 fingerprint. May optionally include the <c>SHA256:</c> prefix.
  /// Obtain it by running <c>ssh-keyscan -t ed25519 &lt;host&gt; | ssh-keygen -lf -</c>
  /// or inspecting the Aspire console on first connection (logged at Trace level).
  /// </param>
  public static IResourceBuilder<RemoteHostResource> WithHostKeyFingerprint(
    this IResourceBuilder<RemoteHostResource> builder, string sha256Fingerprint)
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentException.ThrowIfNullOrWhiteSpace(sha256Fingerprint);

    // Strip the "SHA256:" prefix so we store only the base64 portion.
    const string prefix = "SHA256:";
    builder.Resource.HostKeyFingerprint = sha256Fingerprint.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
      ? sha256Fingerprint[prefix.Length..]
      : sha256Fingerprint;

    return builder;
  }

  /// <summary>
  /// Pins the vsdbg version to install on the remote host.
  /// Defaults to <c>"latest"</c> when not configured, which carries supply-chain risk.
  /// Pin to a specific version (e.g. <c>"17.13.30618.01"</c>) for reproducible installs.
  /// Version must be "latest" or semantic versioning format (X.Y.Z).
  /// </summary>
  public static IResourceBuilder<RemoteHostResource> WithVsdbgVersion(
    this IResourceBuilder<RemoteHostResource> builder, string version)
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentException.ThrowIfNullOrWhiteSpace(version);

    // Validate version format: "latest" or semantic version X.Y.Z (dots and digits only)
    if (!version.Equals("latest", StringComparison.OrdinalIgnoreCase)
        && !System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+$"))
    {
      throw new ArgumentException(
        $"vsdbg version must be 'latest' or semantic version format 'X.Y.Z', but got '{version}'.",
        nameof(version));
    }

    builder.Resource.VsdbgVersion = version;
    return builder;
  }

}
