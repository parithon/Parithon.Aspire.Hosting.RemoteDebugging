using System.Runtime.InteropServices;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost;
using Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost.Annotations;
using Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;
using Parithon.Aspire.Hosting.RemoteDebugging.RemoteProject;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Parithon.Aspire.Hosting.RemoteDebugging.Tests.RemoteProject;

/// <summary>
/// Unit tests for <see cref="RemoteProjectRunner"/> covering the
/// <c>BuildEnvironmentAsync</c> path: Aspire <see cref="EnvironmentCallbackAnnotation"/>
/// invocation, endpoint tunnel setup, and environment variable priority.
/// </summary>
[TestClass]
public class BuildEnvironmentTests
{
  // ── Helpers ───────────────────────────────────────────────────────────────

  private static (IDistributedApplicationBuilder appBuilder, IResourceBuilder<RemoteProjectResource<FakeProject>> projectBuilder)
    CreateProjectBuilder(string hostName = "dev-host", string projectName = "remote-worker")
  {
    var appBuilder     = DistributedApplication.CreateBuilder();
    var passwordParam  = appBuilder.AddParameter("password", secret: true);
    var credential     = new RemoteHostCredential("user", passwordParam);
    var hostBuilder    = appBuilder.AddRemoteHost(hostName, OSPlatform.Linux, credential);
    var projectBuilder = appBuilder.AddRemoteProject<FakeProject>(projectName, hostBuilder);
    return (appBuilder, projectBuilder);
  }

  private static Mock<IRemoteHostTransport> CreateMockTransport(uint tunnelPort = 40001)
  {
    var mock = new Mock<IRemoteHostTransport>();
    mock.Setup(t => t.StartEndpointTunnelAsync(
        It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<ILogger>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync(tunnelPort);
    return mock;
  }

  private static void AttachTransport(RemoteHostResource host, IRemoteHostTransport transport)
    => host.Annotations.Add(new RemoteHostTransportAnnotation(transport));

  // ── Default Aspire env vars always present ────────────────────────────────

  [TestMethod]
  public async Task BuildEnvironment_AlwaysContainsDefaultEnvVars()
  {
    var (_, projectBuilder) = CreateProjectBuilder();

    var env = await RemoteProjectRunner.BuildEnvironmentAsync(
      projectBuilder.Resource, transport: null, NullLogger.Instance, CancellationToken.None);

    env.Should().ContainKey("ASPNETCORE_ENVIRONMENT").WhoseValue.Should().Be("Development");
    env.Should().ContainKey("DOTNET_ENVIRONMENT").WhoseValue.Should().Be("Development");
  }

  // ── WithEnvironment (plain string) ────────────────────────────────────────

  [TestMethod]
  public async Task BuildEnvironment_WithEnvironmentStringCallback_IsInjected()
  {
    var (_, projectBuilder) = CreateProjectBuilder();
    projectBuilder.WithEnvironment("MY_VAR", "hello-world");

    var env = await RemoteProjectRunner.BuildEnvironmentAsync(
      projectBuilder.Resource, transport: null, NullLogger.Instance, CancellationToken.None);

    env.Should().ContainKey("MY_VAR").WhoseValue.Should().Be("hello-world");
  }

  [TestMethod]
  public async Task BuildEnvironment_WithEnvironmentRegistersCallback_IsInjectedWithoutInternalDictionary()
  {
    var (_, projectBuilder) = CreateProjectBuilder();
    projectBuilder.WithEnvironment("MY_VAR", "from-callback");

    // Simulate a run that only relies on Aspire callback annotations
    // (the path used by dashboard environment capture).
    projectBuilder.Resource.EnvironmentVariables.Clear();

    var env = await RemoteProjectRunner.BuildEnvironmentAsync(
      projectBuilder.Resource, transport: null, NullLogger.Instance, CancellationToken.None);

    env.Should().ContainKey("MY_VAR").WhoseValue.Should().Be("from-callback");
  }

  // ── User env vars override callback-injected values ───────────────────────

  [TestMethod]
  public async Task BuildEnvironment_UserEnvVar_OverridesCallbackValue()
  {
    var (_, projectBuilder) = CreateProjectBuilder();
    // Callback sets MY_VAR = "from-callback".
    projectBuilder.WithEnvironment("MY_VAR", "from-callback");
    // User explicitly overrides.
    projectBuilder.Resource.EnvironmentVariables["MY_VAR"] = "from-user";

    var env = await RemoteProjectRunner.BuildEnvironmentAsync(
      projectBuilder.Resource, transport: null, NullLogger.Instance, CancellationToken.None);

    env["MY_VAR"].Should().Be("from-user");
  }

  // ── EndpointReference value → tunnel is set up ───────────────────────────

  [TestMethod]
  public async Task BuildEnvironment_EndpointReferenceCallback_SetsTunnelUrl()
  {
    var (_, projectBuilder) = CreateProjectBuilder();
    var transport = CreateMockTransport(tunnelPort: 42000);
    AttachTransport(projectBuilder.Resource.Parent, transport.Object);

    // Manually add an EnvironmentCallbackAnnotation that injects a fake (already-allocated)
    // EndpointReference value, simulating what WithReference(endpointRef) does at runtime.
    var fakeAnnotation = new AllocatedEndpoint(
      new EndpointAnnotation(System.Net.Sockets.ProtocolType.Tcp, uriScheme: "http", name: "http", port: 5000),
      "127.0.0.1",
      5000);

    // Create an EndpointAnnotation with an already-allocated endpoint.
    var endpointAnnotation = new EndpointAnnotation(
      System.Net.Sockets.ProtocolType.Tcp, uriScheme: "http", name: "http", port: 5000);
    endpointAnnotation.AllocatedEndpoint = fakeAnnotation;

    // Build a mock resource with endpoints.
    var mockResource = new Mock<IResourceWithEndpoints>();
    mockResource.Setup(r => r.Name).Returns("api");
    mockResource.Setup(r => r.Annotations).Returns(new ResourceAnnotationCollection());
    var endpointRef = new EndpointReference(mockResource.Object, endpointAnnotation);

    projectBuilder.Resource.Annotations.Add(new EnvironmentCallbackAnnotation(ctx =>
    {
      ctx.EnvironmentVariables["services__api__http__0"] = endpointRef;
      return Task.CompletedTask;
    }));

    var env = await RemoteProjectRunner.BuildEnvironmentAsync(
      projectBuilder.Resource, transport.Object, NullLogger.Instance, CancellationToken.None);

    env.Should().ContainKey("services__api__http__0");
    env["services__api__http__0"].Should().Be("http://127.0.0.1:42000");

    transport.Verify(t => t.StartEndpointTunnelAsync(
      "127.0.0.1", 5000u, It.IsAny<ILogger>(), It.IsAny<CancellationToken>()),
      Times.Once);
  }

  // ── No endpoint tunnels when no WithReference ─────────────────────────────

  [TestMethod]
  public async Task BuildEnvironment_WithoutEndpointCallbacks_NoTunnelSetup()
  {
    var (_, projectBuilder) = CreateProjectBuilder();
    var transport = CreateMockTransport();

    var env = await RemoteProjectRunner.BuildEnvironmentAsync(
      projectBuilder.Resource, transport.Object, NullLogger.Instance, CancellationToken.None);

    transport.Verify(t => t.StartEndpointTunnelAsync(
      It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<ILogger>(), It.IsAny<CancellationToken>()),
      Times.Never);
  }

  // ── Tunnel failure → falls back gracefully ────────────────────────────────

  [TestMethod]
  public async Task BuildEnvironment_TunnelFails_DoesNotThrow()
  {
    var (_, projectBuilder) = CreateProjectBuilder();

    var endpointAnnotation = new EndpointAnnotation(
      System.Net.Sockets.ProtocolType.Tcp, uriScheme: "http", name: "http", port: 5000);
    endpointAnnotation.AllocatedEndpoint = new AllocatedEndpoint(endpointAnnotation, "127.0.0.1", 5000);
    var mockResource = new Mock<IResourceWithEndpoints>();
    mockResource.Setup(r => r.Name).Returns("api");
    mockResource.Setup(r => r.Annotations).Returns(new ResourceAnnotationCollection());
    var endpointRef = new EndpointReference(mockResource.Object, endpointAnnotation);

    projectBuilder.Resource.Annotations.Add(new EnvironmentCallbackAnnotation(ctx =>
    {
      ctx.EnvironmentVariables["services__api__http__0"] = endpointRef;
      return Task.CompletedTask;
    }));

    // Simulate tunnel failure (returns 0).
    var transport = new Mock<IRemoteHostTransport>();
    transport.Setup(t => t.StartEndpointTunnelAsync(
        It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<ILogger>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync(0u);
    AttachTransport(projectBuilder.Resource.Parent, transport.Object);

    var act = async () => await RemoteProjectRunner.BuildEnvironmentAsync(
      projectBuilder.Resource, transport.Object, NullLogger.Instance, CancellationToken.None);

    await act.Should().NotThrowAsync();
  }

  // ── WithReference compiles against RemoteProjectResource ─────────────────

  [TestMethod]
  public void RemoteProjectResource_ImplementsIResourceWithEnvironment()
  {
    var (_, projectBuilder) = CreateProjectBuilder();
    projectBuilder.Resource.Should().BeAssignableTo<IResourceWithEnvironment>();
  }
}

// ── FakeApiProject stub ──────────────────────────────────────────────────────

/// <summary>Minimal <see cref="IProjectMetadata"/> stub for a local "API" project in tests.</summary>
public sealed class FakeApiProject : IProjectMetadata
{
  public string ProjectPath => "FakeApiProject.csproj";
}
