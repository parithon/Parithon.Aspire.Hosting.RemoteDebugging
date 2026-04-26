using Parithon.Aspire.Hosting.RemoteDebugging.Sidecar.Application;
using Parithon.Aspire.Hosting.RemoteDebugging.Sidecar.Domain;
using Microsoft.Extensions.Options;

namespace Parithon.Aspire.Hosting.RemoteDebugging.Sidecar.Infrastructure;

/// <summary>
/// Writes a timestamped log dump file containing the cached stdout/stderr from every
/// <see cref="ManagedProcess"/> when the connection to the AppHost times out.
/// </summary>
internal sealed class LogCachePersistence(
  ILogger<LogCachePersistence> logger,
  IOptions<SidecarOptions> options)
{
  /// <summary>
  /// Saves a snapshot of every process's log cache to a single file in
  /// <see cref="SidecarOptions.LogDumpDirectory"/> (or <see cref="Path.GetTempPath"/> if not set).
  /// </summary>
  public async Task SaveAsync(
    IReadOnlyList<ManagedProcess> processes,
    CancellationToken cancellationToken = default)
  {
    if (processes.Count == 0)
      return;

    var outputDir = options.Value.LogDumpDirectory ?? Path.GetTempPath();
    var fileName  = $"sidecar-cache-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.log";
    var filePath  = Path.Combine(outputDir, fileName);

    try
    {
      await using var writer = new StreamWriter(filePath, append: false);

      await writer.WriteLineAsync(
        $"# Sidecar log dump — {DateTimeOffset.UtcNow:O}").ConfigureAwait(false);
      await writer.WriteLineAsync(
        "# AppHost connection timed out; cached stdout/stderr follows.").ConfigureAwait(false);

      foreach (var process in processes)
      {
        cancellationToken.ThrowIfCancellationRequested();

        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync(
          $"## {process.Name}  PID={process.Pid}  State={process.State}").ConfigureAwait(false);

        var snapshot = process.LogBuffer.GetSnapshot();
        if (snapshot.Count == 0)
        {
          await writer.WriteLineAsync("   (no cached lines)").ConfigureAwait(false);
          continue;
        }

        foreach (var entry in snapshot)
        {
          var prefix = entry.IsError ? "[ERR]" : "[OUT]";
          await writer.WriteLineAsync(
            $"{entry.Timestamp:O} {prefix} {entry.Content}").ConfigureAwait(false);
        }
      }

      logger.LogInformation(
        "Cached logs ({ProcessCount} process(es)) saved to '{FilePath}'.",
        processes.Count, filePath);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      logger.LogError(ex, "Failed to write log dump to '{FilePath}'.", filePath);
    }
  }
}
