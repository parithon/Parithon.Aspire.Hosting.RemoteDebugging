using Aspire.Hosting.ApplicationModel;

namespace Parithon.Aspire.Hosting.RemoteDebugging.RemoteProject.Annotations;

/// <summary>
/// Configures log file tailing for a <see cref="RemoteProjectResource{TProject}"/> that runs
/// as a Windows Service via <see cref="WindowsServiceAnnotation"/>.
/// </summary>
/// <remarks>
/// <para>
/// When present, <c>WindowsServiceRunner</c> uploads a PowerShell <c>Get-Content -Wait</c>
/// tailer script to the remote host and starts it as a sidecar-managed process. Each log line
/// is streamed to the Aspire console; lines that match the derived error/fatal pattern are
/// routed to stderr so they appear as errors in the dashboard.
/// </para>
/// <para>
/// Add via <c>WithLoggingSupport()</c> — do not construct directly.
/// </para>
/// </remarks>
public sealed class LoggingSupportAnnotation(string logFilePath) : IResourceAnnotation
{
  /// <summary>Absolute path to the log file on the remote host (e.g. <c>C:\Windows\Logs\app\app.log</c>).</summary>
  public string LogFilePath { get; } = logFilePath;

  /// <summary>
  /// Optional Serilog <c>outputTemplate</c> used to derive the error-level detection pattern.
  /// Supports <c>{Level:u3}</c>, <c>{Level:u4}</c>, <c>{Level:w}</c>, and plain <c>{Level}</c>.
  /// When <see langword="null"/> a conservative fallback pattern matching all common error-level
  /// representations is used.
  /// </summary>
  public string? OutputTemplate { get; init; }
}
