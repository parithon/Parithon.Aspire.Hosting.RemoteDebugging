using System.Reflection;

namespace Aspire.Hosting.RemoteDebugging.RemoteHost;

/// <summary>
/// Extracts the embedded <c>aspire-sidecar</c> artifacts from the hosting library
/// to a temp directory so they can be uploaded to the remote host.
/// </summary>
internal static class SidecarExtractor
{
  private const string ResourcePrefix = "aspire-sidecar/";

  /// <summary>
  /// Extracts all embedded sidecar resources to a temporary directory and returns
  /// the path to that directory.  Subsequent calls with unchanged assemblies return
  /// the same directory without re-extracting.
  /// </summary>
  public static string ExtractToTempDirectory()
  {
    var outputDir = Path.Combine(Path.GetTempPath(), "aspire-sidecar-deploy");
    Directory.CreateDirectory(outputDir);

    var assembly = typeof(SidecarExtractor).Assembly;
    foreach (var name in assembly.GetManifestResourceNames())
    {
      if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
        continue;

      var fileName = name[ResourcePrefix.Length..];
      var destPath = Path.Combine(outputDir, fileName);

      // Skip if the embedded resource hasn't changed (avoid unnecessary writes).
      using var resourceStream = assembly.GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"Embedded resource '{name}' could not be opened.");

      if (File.Exists(destPath) && new FileInfo(destPath).Length == resourceStream.Length)
        continue;

      using var fileStream = File.Create(destPath);
      resourceStream.CopyTo(fileStream);
    }

    return outputDir;
  }
}
