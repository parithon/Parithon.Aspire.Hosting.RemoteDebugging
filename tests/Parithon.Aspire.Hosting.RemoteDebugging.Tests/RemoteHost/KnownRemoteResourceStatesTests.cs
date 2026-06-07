using Aspire.Hosting.ApplicationModel;
using FluentAssertions;
using Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost.HealthChecks;

namespace Parithon.Aspire.Hosting.RemoteDebugging.Tests.RemoteHost;

[TestClass]
public class KnownRemoteResourceStatesTests
{
  [TestMethod]
  public void Snapshots_UseBuiltInAspireStates()
  {
    KnownRemoteResourceStates.DisconnectedSnapshot.Text.Should().Be(KnownResourceStates.NotStarted);
    KnownRemoteResourceStates.ConnectingSnapshot.Text.Should().Be(KnownResourceStates.Starting);
    KnownRemoteResourceStates.ConnectedSnapshot.Text.Should().Be(KnownResourceStates.Running);
    KnownRemoteResourceStates.RunningSnapshot.Text.Should().Be(KnownResourceStates.Running);
    KnownRemoteResourceStates.ReconnectingSnapshot.Text.Should().Be(KnownResourceStates.Waiting);
    KnownRemoteResourceStates.DisconnectingSnapshot.Text.Should().Be(KnownResourceStates.Stopping);
    KnownRemoteResourceStates.FailedToConnectSnapshot.Text.Should().Be(KnownResourceStates.FailedToStart);
    KnownRemoteResourceStates.FailedToInitializeSnapshot.Text.Should().Be(KnownResourceStates.FailedToStart);
    KnownRemoteResourceStates.ExitedSnapshot.Text.Should().Be(KnownResourceStates.Exited);
  }

  [TestMethod]
  public void Snapshots_UseSupportedAspireStyles()
  {
    KnownRemoteResourceStates.DisconnectedSnapshot.Style.Should().BeNull();
    KnownRemoteResourceStates.ConnectingSnapshot.Style.Should().Be(KnownResourceStateStyles.Info);
    KnownRemoteResourceStates.ConnectedSnapshot.Style.Should().Be(KnownResourceStateStyles.Success);
    KnownRemoteResourceStates.RunningSnapshot.Style.Should().Be(KnownResourceStateStyles.Success);
    KnownRemoteResourceStates.ReconnectingSnapshot.Style.Should().Be(KnownResourceStateStyles.Warn);
    KnownRemoteResourceStates.DisconnectingSnapshot.Style.Should().Be(KnownResourceStateStyles.Info);
    KnownRemoteResourceStates.FailedToConnectSnapshot.Style.Should().Be(KnownResourceStateStyles.Error);
    KnownRemoteResourceStates.FailedToInitializeSnapshot.Style.Should().Be(KnownResourceStateStyles.Error);
    KnownRemoteResourceStates.InstallingToolsSnapshot.Style.Should().Be(KnownResourceStateStyles.Info);
    KnownRemoteResourceStates.DeployingSidecarSnapshot.Style.Should().Be(KnownResourceStateStyles.Info);
    KnownRemoteResourceStates.StartingSidecarSnapshot.Style.Should().Be(KnownResourceStateStyles.Info);
  }
}