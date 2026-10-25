using Aspire.Hosting.ApplicationModel;

namespace Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost.HealthChecks;

public static class KnownRemoteResourceStates
{
  public const string Disconnecting = "Disconnecting";
  public const string Disconnected = "Disconnected";
  public const string Connecting = "Connecting";
  public const string Connected = "Connected";
  public const string Reconnecting = "Reconnecting";
  public const string FailedToConnect = "Connection failed";
  public const string FailedToInitialize = "Failed initialization";

  public const string InstallRemoteTools = "Installing tools";
  public const string DeployingSidecar   = "Deploying sidecar";
  public const string StartingSidecar    = "Starting sidecar";

  public static string? GetStyle(string state)
  {
    if (state == Connecting || state == KnownResourceStates.Starting || state == InstallRemoteTools || state == DeployingSidecar || state == StartingSidecar)
    {
      return KnownResourceStateStyles.Info;
    }

    if (state == Connected || state == KnownResourceStates.Running)
    {
      return KnownResourceStateStyles.Success;
    }

    if (state == Reconnecting || state == KnownResourceStates.Waiting)
    {
      return KnownResourceStateStyles.Warn;
    }

    if (state == Disconnecting || state == KnownResourceStates.Stopping)
    {
      return KnownResourceStateStyles.Info;
    }

    if (state == FailedToConnect || state == KnownResourceStates.FailedToStart)
    {
      return KnownResourceStateStyles.Error;
    }

    return null;
  }

  public static readonly ResourceStateSnapshot DisconnectingSnapshot      = new(KnownResourceStates.Stopping, GetStyle(KnownResourceStates.Stopping));
  public static readonly ResourceStateSnapshot DisconnectedSnapshot       = new(KnownResourceStates.NotStarted, GetStyle(KnownResourceStates.NotStarted));
  public static readonly ResourceStateSnapshot ConnectingSnapshot         = new(KnownResourceStates.Starting, GetStyle(KnownResourceStates.Starting));
  public static readonly ResourceStateSnapshot ConnectedSnapshot          = new(KnownResourceStates.Running, GetStyle(KnownResourceStates.Running));
  public static readonly ResourceStateSnapshot RunningSnapshot            = new(KnownResourceStates.Running, KnownResourceStateStyles.Success);
  public static readonly ResourceStateSnapshot ReconnectingSnapshot       = new(KnownResourceStates.Waiting, GetStyle(KnownResourceStates.Waiting));
  public static readonly ResourceStateSnapshot FailedToConnectSnapshot    = new(KnownResourceStates.FailedToStart, GetStyle(KnownResourceStates.FailedToStart));
  public static readonly ResourceStateSnapshot ExitedSnapshot             = new(KnownResourceStates.Exited, null);
  public static readonly ResourceStateSnapshot InstallingToolsSnapshot    = new(KnownResourceStates.Starting, GetStyle(KnownResourceStates.Starting));
  public static readonly ResourceStateSnapshot DeployingSidecarSnapshot   = new(KnownResourceStates.Starting, GetStyle(KnownResourceStates.Starting));
  public static readonly ResourceStateSnapshot StartingSidecarSnapshot    = new(KnownResourceStates.Starting, GetStyle(KnownResourceStates.Starting));
  public static readonly ResourceStateSnapshot FailedToInitializeSnapshot = new(KnownResourceStates.FailedToStart, KnownResourceStateStyles.Error);
}
