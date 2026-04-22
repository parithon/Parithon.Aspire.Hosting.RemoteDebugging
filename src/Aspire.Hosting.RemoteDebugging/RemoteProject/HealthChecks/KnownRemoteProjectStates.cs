using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.RemoteDebugging.RemoteProject.HealthChecks;

public static class KnownRemoteProjectStates
{
  public const string Building       = "Building";
  public const string Deploying      = "Deploying";
  public const string Starting       = "Starting";
  public const string Stopping       = "Stopping";
  public const string FailedToBuild  = "Build failed";
  public const string FailedToDeploy = "Deploy failed";
  public const string FailedToStart  = "Start failed";

  public static string? GetStyle(string state) => state switch
  {
    Building       => KnownResourceStateStyles.Info,
    Deploying      => KnownResourceStateStyles.Info,
    Starting       => KnownResourceStateStyles.Info,
    Stopping       => KnownResourceStateStyles.Info,
    FailedToBuild  => KnownResourceStateStyles.Error,
    FailedToDeploy => KnownResourceStateStyles.Error,
    FailedToStart  => KnownResourceStateStyles.Error,
    _              => null
  };

  public static readonly ResourceStateSnapshot BuildingSnapshot       = new(Building,       GetStyle(Building));
  public static readonly ResourceStateSnapshot DeployingSnapshot      = new(Deploying,      GetStyle(Deploying));
  public static readonly ResourceStateSnapshot StartingSnapshot       = new(Starting,       GetStyle(Starting));
  public static readonly ResourceStateSnapshot StoppingSnapshot       = new(Stopping,       GetStyle(Stopping));
  public static readonly ResourceStateSnapshot FailedToBuildSnapshot  = new(FailedToBuild,  GetStyle(FailedToBuild));
  public static readonly ResourceStateSnapshot FailedToDeploySnapshot = new(FailedToDeploy, GetStyle(FailedToDeploy));
  public static readonly ResourceStateSnapshot FailedToStartSnapshot  = new(FailedToStart,  GetStyle(FailedToStart));
  public static readonly ResourceStateSnapshot RunningSnapshot        = new(KnownResourceStates.Running, KnownResourceStateStyles.Success);
  public static readonly ResourceStateSnapshot ExitedSnapshot         = new(KnownResourceStates.Exited,  null);
}
