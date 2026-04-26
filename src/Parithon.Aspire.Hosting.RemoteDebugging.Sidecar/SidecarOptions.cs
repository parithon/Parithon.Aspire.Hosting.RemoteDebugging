namespace Parithon.Aspire.Hosting.RemoteDebugging.Sidecar;

/// <summary>
/// Configuration options for the sidecar gRPC process manager.
/// Bound from the <c>Sidecar</c> configuration section.
/// </summary>
internal sealed class SidecarOptions
{
  public const string SectionName = "Sidecar";

  /// <summary>TCP port the gRPC server listens on. Defaults to <c>5055</c>.</summary>
  public int Port { get; init; } = 5055;

  /// <summary>
  /// How long the sidecar waits with no active gRPC connections before declaring the
  /// AppHost permanently gone, dumping cached logs, stopping child processes, and exiting.
  /// Defaults to 5 minutes.
  /// </summary>
  public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromMinutes(5);

  /// <summary>
  /// How long stdout/stderr lines are kept in the in-memory log cache while no streaming
  /// client is reading them.  Lines older than this window are pruned on every append.
  /// Defaults to 5 minutes.
  /// </summary>
  public TimeSpan LogCacheRetention { get; init; } = TimeSpan.FromMinutes(5);

  /// <summary>
  /// Directory where the log dump file is written when the connection times out.
  /// Defaults to <see cref="Path.GetTempPath()"/> when <see langword="null"/>.
  /// </summary>
  public string? LogDumpDirectory { get; init; }
}
