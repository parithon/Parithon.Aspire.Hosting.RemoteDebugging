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

  public static string? GetStyle(string state) => state switch
  {
    Connecting    => KnownResourceStateStyles.Info,
    Connected     => KnownResourceStateStyles.Success,
    Reconnecting  => KnownResourceStateStyles.Warn,
    Disconnecting => KnownResourceStateStyles.Info,
    FailedToConnect => KnownResourceStateStyles.Error,
    InstallRemoteTools => KnownResourceStateStyles.Info,
    DeployingSidecar   => KnownResourceStateStyles.Info,
    StartingSidecar    => KnownResourceStateStyles.Info,
    _ => null // Disconnected
  };

  public static readonly ResourceStateSnapshot DisconnectingSnapshot      = new(Disconnecting, GetStyle(Disconnecting));
  public static readonly ResourceStateSnapshot DisconnectedSnapshot       = new(Disconnected, GetStyle(Disconnected));
  public static readonly ResourceStateSnapshot ConnectingSnapshot         = new(Connecting, GetStyle(Connecting));
  public static readonly ResourceStateSnapshot ConnectedSnapshot          = new(Connected, GetStyle(Connected));
  public static readonly ResourceStateSnapshot RunningSnapshot            = new(KnownResourceStates.Running, KnownResourceStateStyles.Success);
  public static readonly ResourceStateSnapshot ReconnectingSnapshot       = new(Reconnecting, GetStyle(Reconnecting));
  public static readonly ResourceStateSnapshot FailedToConnectSnapshot    = new(FailedToConnect, GetStyle(FailedToConnect));
  public static readonly ResourceStateSnapshot ExitedSnapshot             = new(KnownResourceStates.Exited, null);
  public static readonly ResourceStateSnapshot InstallingToolsSnapshot    = new(InstallRemoteTools, GetStyle(InstallRemoteTools));
  public static readonly ResourceStateSnapshot DeployingSidecarSnapshot   = new(DeployingSidecar, GetStyle(DeployingSidecar));
  public static readonly ResourceStateSnapshot StartingSidecarSnapshot    = new(StartingSidecar, GetStyle(StartingSidecar));
  public static readonly ResourceStateSnapshot FailedToInitializeSnapshot = new("Failed initialization", KnownResourceStateStyles.Error);
}
