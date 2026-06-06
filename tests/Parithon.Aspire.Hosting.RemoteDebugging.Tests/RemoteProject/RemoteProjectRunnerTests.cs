using System.Reflection;
using System.Runtime.InteropServices;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost;
using Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost.Annotations;
using Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;
using Parithon.Aspire.Hosting.RemoteDebugging.RemoteProject;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Parithon.Aspire.Hosting.RemoteDebugging.Tests.RemoteProject;

[TestClass]
public class RemoteProjectRunnerTests
{
  [TestMethod]
  public async Task PrepareAndDeployAsync_PostReconnectWindowsServiceRedeploy_CleansBeforeDeploy()
  {
    var appBuilder = DistributedApplication.CreateBuilder();
    var passwordParam = appBuilder.AddParameter("password", secret: true);
    var credential = new RemoteHostCredential("user", passwordParam);
    var hostBuilder = appBuilder.AddRemoteHost("win-dev", OSPlatform.Windows, credential);
    hostBuilder.Resource.DeploymentPath = @"C:/remote";

    var projectBuilder = hostBuilder.AddRemoteProject<FakeProject>("remote-worker");
    projectBuilder.AsWindowsService("remoteworker");

    var transport = new Mock<IRemoteHostTransport>(MockBehavior.Strict);
    hostBuilder.Resource.Annotations.Add(new RemoteHostTransportAnnotation(transport.Object));

    SetInternalProperty(projectBuilder.Resource, "BuildOutputPath", @"C:/artifacts/remote-worker");

    var sequence = new MockSequence();
    transport.InSequence(sequence)
      .Setup(t => t.ExecuteSshCommandAsync("sc.exe query \"remoteworker\"", It.IsAny<CancellationToken>()))
      .ReturnsAsync((0, "SERVICE_NAME: remoteworker\nSTATE              : 4  RUNNING", string.Empty));
    transport.InSequence(sequence)
      .Setup(t => t.ExecuteSshCommandAsync("sc.exe stop \"remoteworker\"", It.IsAny<CancellationToken>()))
      .ReturnsAsync((0, string.Empty, string.Empty));
    transport.InSequence(sequence)
      .Setup(t => t.ExecuteSshCommandAsync("sc.exe query \"remoteworker\"", It.IsAny<CancellationToken>()))
      .ReturnsAsync((0, "SERVICE_NAME: remoteworker\nSTATE              : 1  STOPPED", string.Empty));
    transport.InSequence(sequence)
      .Setup(t => t.ExecuteSshCommandAsync("sc.exe delete \"remoteworker\"", It.IsAny<CancellationToken>()))
      .ReturnsAsync((0, string.Empty, string.Empty));
    transport.InSequence(sequence)
      .Setup(t => t.DeployDirectoryAsync(@"C:/artifacts/remote-worker", "C:/remote/remote-worker", It.IsAny<Microsoft.Extensions.Logging.ILogger>(), It.IsAny<CancellationToken>()))
      .Returns(Task.CompletedTask);

    await InvokePrepareAndDeployAsync(projectBuilder.Resource);

    GetInternalProperty<string>(projectBuilder.Resource, "RemoteDeploymentPath")
      .Should().Be("C:/remote/remote-worker");

    transport.VerifyAll();
  }

  private static async Task InvokePrepareAndDeployAsync(RemoteProjectResource<FakeProject> resource)
  {
    var method = typeof(RemoteProjectRunner)
      .GetMethod("PrepareAndDeployAsync", BindingFlags.Static | BindingFlags.NonPublic)
      ?? throw new InvalidOperationException("PrepareAndDeployAsync was not found via reflection.");

    var genericMethod = method.MakeGenericMethod(typeof(FakeProject));
    var task = genericMethod.Invoke(null, [resource, NullLogger.Instance, CancellationToken.None]) as Task
      ?? throw new InvalidOperationException("PrepareAndDeployAsync did not return a Task.");

    await task.ConfigureAwait(false);
  }

  private static void SetInternalProperty<T>(object target, string propertyName, T value)
  {
    var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic)
      ?? throw new InvalidOperationException($"Property '{propertyName}' was not found.");

    property.SetValue(target, value);
  }

  private static T? GetInternalProperty<T>(object target, string propertyName)
  {
    var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic)
      ?? throw new InvalidOperationException($"Property '{propertyName}' was not found.");

    return (T?)property.GetValue(target);
  }
}