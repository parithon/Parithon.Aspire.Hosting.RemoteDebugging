using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.RemoteDebugging;

public static class KnownRemoteResourceStates
{
  public const string Disconnecting = "Disconnecting";
  public const string Disconnected = "Disconnected";
  public const string Connecting = "Connecting";
  public const string Connected = "Connected";
  public const string Reconnecting = "Reconnecting";
  public const string FailedToConnect = "Connection failed";

  public static string? GetStyle(string state) => state switch
  {
    Connecting    => KnownResourceStateStyles.Info,
    Connected     => KnownResourceStateStyles.Success,
    Reconnecting  => KnownResourceStateStyles.Warn,
    Disconnecting => KnownResourceStateStyles.Info,
    FailedToConnect => KnownResourceStateStyles.Error,
    _ => null // Disconnected
  };

  public static readonly ResourceStateSnapshot DisconnectingSnapshot    = new(Disconnecting, GetStyle(Disconnecting));
  public static readonly ResourceStateSnapshot DisconnectedSnapshot     = new(Disconnected, GetStyle(Disconnected));
  public static readonly ResourceStateSnapshot ConnectingSnapshot       = new(Connecting, GetStyle(Connecting));
  public static readonly ResourceStateSnapshot ConnectedSnapshot        = new(Connected, GetStyle(Connected));
  public static readonly ResourceStateSnapshot ReconnectingSnapshot     = new(Reconnecting, GetStyle(Reconnecting));
  public static readonly ResourceStateSnapshot FailedToConnectSnapshot  = new(FailedToConnect, GetStyle(FailedToConnect));
  public static readonly ResourceStateSnapshot InstallRemoteDebuggerSnapshot = new("Initializing", KnownResourceStateStyles.Info);
  public static readonly ResourceStateSnapshot FailedToInitializeSnapshot = new("Failed initialization", KnownResourceStateStyles.Error);
}
