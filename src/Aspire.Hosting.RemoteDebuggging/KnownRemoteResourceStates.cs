using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.RemoteDebuggging;

public static class KnownRemoteResourceStates
{
  public static readonly string Disconnecting = "Disconnecting";
  public static readonly string Disconnected = "Disconnected";
  public static readonly string Connecting = "Connecting";
  public static readonly string Connected = "Connected";
  public static readonly string Reconnecting = "Reconnecting";
  public static readonly string FailedToConnect = "Connection failed";

  public static string? GetStyle(string state) => state switch
  {
    var s when s == Connecting => KnownResourceStateStyles.Info,
    var s when s == Connected => KnownResourceStateStyles.Success,
    var s when s == Reconnecting => KnownResourceStateStyles.Warn,
    var s when s == Disconnecting => KnownResourceStateStyles.Info,
    var s when s == FailedToConnect => KnownResourceStateStyles.Error,
    _ => null // Disconnected
  };

  public static readonly ResourceStateSnapshot DisconnectingSnapshot = new(Disconnecting, GetStyle(Disconnecting));
  public static readonly ResourceStateSnapshot DisconnectedSnapshot  = new(Disconnected, GetStyle(Disconnected));
  public static readonly ResourceStateSnapshot ConnectingSnapshot    = new(Connecting, GetStyle(Connecting));
  public static readonly ResourceStateSnapshot ConnectedSnapshot     = new(Connected, GetStyle(Connected));
  public static readonly ResourceStateSnapshot FailedToConnectSnapshot = new(FailedToConnect, GetStyle(FailedToConnect));
}
