using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteDebugging.RemoteHost.Annotations;
using Aspire.Hosting.RemoteDebugging.RemoteProject.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteDebugging.RemoteProject;

internal static class RemoteProjectRunner
{
  internal static async Task RunAsync<TProject>(RemoteProjectResource<TProject> resource, ResourceNotificationService notifications, ResourceLoggerService loggers, CancellationToken cancellationToken) where TProject : IProjectMetadata
  {
    await resource.RunGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      await RunCoreAsync(resource, notifications, loggers, cancellationToken).ConfigureAwait(false);
    }
    finally
    {
      resource.RunGate.Release();
    }
  }

  private static async Task RunCoreAsync<TProject>(RemoteProjectResource<TProject> resource, ResourceNotificationService notifications, ResourceLoggerService loggers, CancellationToken cancellationToken) where TProject : IProjectMetadata
  {
    var logger = loggers.GetLogger(resource);

    // Phase 1: Build
    cancellationToken.ThrowIfCancellationRequested();
    await notifications.PublishUpdateAsync(resource, s => s with
    {
      State = KnownRemoteProjectStates.BuildingSnapshot,
      StartTimeStamp = DateTime.UtcNow
    }).ConfigureAwait(false);

    try
    {
      await BuildAsync(resource, logger, cancellationToken).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      logger.LogError(ex, "Failed to build project for {Name}", resource.Name);
      await notifications.PublishUpdateAsync(resource, s => s with
      {
        State = KnownRemoteProjectStates.FailedToBuildSnapshot,
        StopTimeStamp = DateTime.UtcNow
      }).ConfigureAwait(false);
      return;
    }

    // Phase 2: Deploy
    cancellationToken.ThrowIfCancellationRequested();
    await notifications.PublishUpdateAsync(resource, s => s with
    {
      State = KnownRemoteProjectStates.DeployingSnapshot
    }).ConfigureAwait(false);

    try
    {
      await DeployAsync(resource, logger, cancellationToken).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      logger.LogError(ex, "Failed to deploy project for {Name}", resource.Name);
      await notifications.PublishUpdateAsync(resource, s => s with
      {
        State = KnownRemoteProjectStates.FailedToDeploySnapshot,
        StopTimeStamp = DateTime.UtcNow
      }).ConfigureAwait(false);
      return;
    }

    // Phase 3: Start
    cancellationToken.ThrowIfCancellationRequested();
    await notifications.PublishUpdateAsync(resource, s => s with
    {
      State = KnownRemoteProjectStates.StartingSnapshot
    }).ConfigureAwait(false);

    try
    {
      await StartAsync(resource, logger, cancellationToken).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      logger.LogError(ex, "Failed to start project for {Name}", resource.Name);
      await notifications.PublishUpdateAsync(resource, s => s with
      {
        State = KnownRemoteProjectStates.FailedToStartSnapshot,
        StopTimeStamp = DateTime.UtcNow
      }).ConfigureAwait(false);
      return;
    }

    cancellationToken.ThrowIfCancellationRequested();
    await notifications.PublishUpdateAsync(resource, s => s with
    {
      State = KnownRemoteProjectStates.RunningSnapshot
    }).ConfigureAwait(false);
  }

  private static async Task BuildAsync<TProject>(RemoteProjectResource<TProject> resource, ILogger logger, CancellationToken cancellationToken)  where TProject : IProjectMetadata
  {
    TProject metadata;
    try
    {
      metadata = Activator.CreateInstance<TProject>();
    }
    catch (Exception ex)
    {
      throw new InvalidOperationException(
        $"Cannot create project metadata for '{typeof(TProject).Name}'. " +
        "Ensure the type has a public parameterless constructor (Aspire-generated types satisfy this requirement).",
        ex);
    }

    var projectPath = metadata.ProjectPath;
    var projectDir = Path.GetDirectoryName(projectPath)
      ?? throw new InvalidOperationException(
           $"Cannot determine directory for project path '{projectPath}'.");

    var remoteRid = resource.Parent.EffectiveRuntimeIdentifier
      ?? throw new InvalidOperationException(
           $"Cannot build '{resource.Name}': the remote host's Runtime Identifier could not be determined. " +
           "Use '.WithRuntimeIdentifier(...)' on the remote host to set it explicitly.");

    var csprojXml = XDocument.Load(projectPath);

    // Support both <TargetFramework> (single) and <TargetFrameworks> (multi-targeting).
    string tfm;
    var singleTfm = csprojXml.Descendants("TargetFramework").FirstOrDefault()?.Value;
    if (singleTfm is not null)
    {
      tfm = singleTfm;
    }
    else
    {
      var multipleTfms = csprojXml.Descendants("TargetFrameworks").FirstOrDefault()?.Value
        ?? throw new InvalidOperationException(
             $"Cannot determine TargetFramework(s) from '{projectPath}'.");

      var currentTfm = $"net{Environment.Version.Major}.{Environment.Version.Minor}";
      tfm = multipleTfms
        .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault(t => string.Equals(t, currentTfm, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException(
             $"Project '{projectPath}' targets [{multipleTfms}] but none matches the current runtime '{currentTfm}'. " +
             $"Ensure the project includes '{currentTfm}' as a target framework.");
    }

    var localRid = RuntimeInformation.RuntimeIdentifier;
    var crossCompile = !string.Equals(remoteRid, localRid, StringComparison.OrdinalIgnoreCase);

    if (logger.IsEnabled(LogLevel.Information))
    {
      if (crossCompile)
        logger.LogInformation("Cross-compiling for remote RID {RemoteRid} (local: {LocalRid})", remoteRid, localRid);
      else
        logger.LogInformation("Building for RID {LocalRid} (matches remote)", localRid);
    }

    var psi = new ProcessStartInfo("dotnet")
    {
      UseShellExecute = false,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      CreateNoWindow = true,
    };
    psi.ArgumentList.Add("build");
    psi.ArgumentList.Add(projectPath);
    psi.ArgumentList.Add("-c");
    psi.ArgumentList.Add("Debug");
    psi.ArgumentList.Add("-f");
    psi.ArgumentList.Add(tfm);
    if (crossCompile)
    {
      psi.ArgumentList.Add("-r");
      psi.ArgumentList.Add(remoteRid);
    }

    if (logger.IsEnabled(LogLevel.Debug))
    {
      logger.LogDebug("Running: dotnet {Args}", string.Join(' ', psi.ArgumentList));
    }

    using var process = new Process { StartInfo = psi };
    process.Start();

    var stdoutTask = DrainStreamAsync(process.StandardOutput, logger, isError: false);
    var stderrTask = DrainStreamAsync(process.StandardError, logger, isError: true);

    try
    {
      await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
      await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
      await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
      await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
      throw;
    }

    if (process.ExitCode != 0)
      throw new InvalidOperationException(
        $"'dotnet build' failed with exit code {process.ExitCode}. See resource logs for details.");

    resource.BuildOutputPath = crossCompile
      ? Path.Combine(projectDir, "bin", "Debug", tfm, remoteRid)
      : Path.Combine(projectDir, "bin", "Debug", tfm);
  }

  /// <summary>Reads lines from <paramref name="reader"/> until EOF, forwarding each to the logger.</summary>
  private static async Task DrainStreamAsync(StreamReader reader, ILogger logger, bool isError)
  {
    string? line;
    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
    {
      if (isError)
        logger.LogWarning("{Line}", line);
      else
        logger.LogInformation("{Line}", line);
    }
  }

  private static async Task DeployAsync<TProject>(RemoteProjectResource<TProject> resource, ILogger logger, CancellationToken cancellationToken) where TProject : IProjectMetadata
  {
    if (resource.BuildOutputPath is not string buildOutput)
      throw new InvalidOperationException($"Cannot deploy '{resource.Name}': BuildOutputPath is not set.");

    if (!resource.Parent.TryGetLastAnnotation<RemoteHostTransportAnnotation>(out var annotation) || annotation is null)
      throw new InvalidOperationException($"Cannot deploy '{resource.Name}': no active transport on the remote host.");

    // Pass the raw DeploymentPath — SshTransport.DeployDirectoryAsync normalises to SFTP format internally.
    var basePath = resource.Parent.DeploymentPath!;
    var remotePath = $"{basePath}/{resource.Name}";
    
    if (logger.IsEnabled(LogLevel.Information))
    {
      logger.LogInformation("Deploying {Name} to {RemotePath}", resource.Name, remotePath);
    }

    await annotation.Transport.DeployDirectoryAsync(buildOutput, remotePath, logger, cancellationToken)
      .ConfigureAwait(false);

    resource.RemoteDeploymentPath = remotePath;
  }

  private static Task StartAsync<TProject>(RemoteProjectResource<TProject> resource, ILogger logger, CancellationToken cancellationToken) where TProject : IProjectMetadata
  {
    // TODO (M5/M6): launch remote process via SSH transport
    return Task.CompletedTask;
  }
}

