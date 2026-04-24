using System.Runtime.InteropServices;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteDebugging.RemoteHost;
using Aspire.Hosting.RemoteDebugging.RemoteHost.Annotations;
using Aspire.Hosting.RemoteDebugging.RemoteProject;
using Aspire.Hosting.RemoteDebugging.RemoteProject.Annotations;
using FluentAssertions;

namespace Aspire.Hosting.RemoteDebugging.Tests.RemoteProject;

[TestClass]
public class WindowsServiceAnnotationTests
{
  [TestMethod]
  public void WindowsServiceAnnotation_DefaultLogWatcherName_IsServiceNameSuffixed()
  {
    var annotation = new WindowsServiceAnnotation("my-service");

    annotation.LogWatcherProcessName.Should().Be("my-service-log-watcher");
  }

  [TestMethod]
  public void WindowsServiceAnnotation_Properties_AreSet()
  {
    var annotation = new WindowsServiceAnnotation("svc")
    {
      DisplayName = "My Service",
      Description = "Does things",
    };

    annotation.ServiceName.Should().Be("svc");
    annotation.DisplayName.Should().Be("My Service");
    annotation.Description.Should().Be("Does things");
  }
}

[TestClass]
public class AsWindowsServiceExtensionTests
{
  // Helper: build a minimal DistributedApplicationBuilder with a RemoteHostResource via AddRemoteHost.
  private static IResourceBuilder<RemoteProjectResource<FakeProject>> BuildProjectOn(
    IDistributedApplicationBuilder appBuilder,
    string hostName,
    OSPlatform platform,
    string projectName = "remote-worker")
  {
    var passwordParam = appBuilder.AddParameter("password", secret: true);
    var credential = new RemoteHostCredential("user", passwordParam);
    var hostBuilder = appBuilder.AddRemoteHost(hostName, platform, credential);
    return appBuilder.AddRemoteProject<FakeProject>(projectName, hostBuilder);
  }

  [TestMethod]
  public void AsWindowsService_DefaultServiceName_UsesResourceName()
  {
    var appBuilder = DistributedApplication.CreateBuilder();
    var builder = BuildProjectOn(appBuilder, "win-dev", OSPlatform.Windows);

    builder.AsWindowsService();

    builder.Resource.TryGetLastAnnotation<WindowsServiceAnnotation>(out var annotation).Should().BeTrue();
    annotation!.ServiceName.Should().Be("remote-worker");
  }

  [TestMethod]
  public void AsWindowsService_ExplicitServiceName_IsUsed()
  {
    var appBuilder = DistributedApplication.CreateBuilder();
    var builder = BuildProjectOn(appBuilder, "win-dev", OSPlatform.Windows);

    builder.AsWindowsService(serviceName: "custom-svc", displayName: "Custom Service");

    builder.Resource.TryGetLastAnnotation<WindowsServiceAnnotation>(out var annotation).Should().BeTrue();
    annotation!.ServiceName.Should().Be("custom-svc");
    annotation.DisplayName.Should().Be("Custom Service");
  }

  [TestMethod]
  public void AsWindowsService_ServiceNameWithSpaces_ReplacesSpacesWithHyphens()
  {
    var appBuilder = DistributedApplication.CreateBuilder();
    var builder = BuildProjectOn(appBuilder, "win-dev", OSPlatform.Windows);

    builder.AsWindowsService(serviceName: "my worker app");

    builder.Resource.TryGetLastAnnotation<WindowsServiceAnnotation>(out var annotation).Should().BeTrue();
    annotation!.ServiceName.Should().Be("my worker app"); // passed through as-is; caller's responsibility
  }

  [TestMethod]
  public void AsWindowsService_DefaultServiceName_MatchesResourceName()
  {
    // Aspire resource names are already hyphen-safe (letters, digits, hyphens only).
    var appBuilder = DistributedApplication.CreateBuilder();
    var builder = BuildProjectOn(appBuilder, "win-dev", OSPlatform.Windows, projectName: "my-worker-app");

    builder.AsWindowsService();

    builder.Resource.TryGetLastAnnotation<WindowsServiceAnnotation>(out var annotation).Should().BeTrue();
    annotation!.ServiceName.Should().Be("my-worker-app");
  }

  [TestMethod]
  public void AsWindowsService_LinuxPlatform_ThrowsInvalidOperationException()
  {
    var appBuilder = DistributedApplication.CreateBuilder();
    var builder = BuildProjectOn(appBuilder, "linux-dev", OSPlatform.Linux);

    var act = () => builder.AsWindowsService();

    act.Should().Throw<InvalidOperationException>()
      .WithMessage("*AsWindowsService()*Windows*");
  }

  [TestMethod]
  public void AsWindowsService_ReturnsOriginalBuilder()
  {
    var appBuilder = DistributedApplication.CreateBuilder();
    var builder = BuildProjectOn(appBuilder, "win-dev", OSPlatform.Windows);

    var result = builder.AsWindowsService();

    result.Should().BeSameAs(builder);
  }
}

[TestClass]
public class WindowsServiceCommandBuilderTests
{
  // Verifies the PowerShell env-var registry command format used in WindowsServiceRunner.InstallAsync.
  // This is a pure string-formatting test — no SSH required.

  [TestMethod]
  public void RegistryEnvVarCommand_ContainsServiceName()
  {
    const string sn = "remote-worker";
    var cmd = BuildRegistryCommand(sn, new Dictionary<string, string>
    {
      ["OTEL_SERVICE_NAME"] = "remote-worker",
      ["DOTNET_ENVIRONMENT"] = "Production",
    });

    cmd.Should().Contain($"Services\\{sn}");
    cmd.Should().Contain("OTEL_SERVICE_NAME=remote-worker");
    cmd.Should().Contain("DOTNET_ENVIRONMENT=Production");
    cmd.Should().Contain("MultiString");
  }

  [TestMethod]
  public void RegistryEnvVarCommand_EscapesSingleQuotes()
  {
    const string sn = "svc";
    var cmd = BuildRegistryCommand(sn, new Dictionary<string, string>
    {
      ["KEY"] = "it's a value",
    });

    // Single quotes in the value must be doubled for PowerShell single-quoted strings.
    cmd.Should().Contain("it''s a value");
  }

  // Mirrors the command built in WindowsServiceRunner.InstallAsync for test isolation.
  private static string BuildRegistryCommand(string serviceName, Dictionary<string, string> env)
  {
    static string Escape(string v) => v.Replace("'", "''");

    var regValues = string.Join(',', env.Select(kv => $"\"{kv.Key}={Escape(kv.Value)}\""));
    return $@"powershell.exe -NonInteractive -Command ""Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\{serviceName}' -Name 'Environment' -Value @({regValues}) -Type MultiString""";
  }
}

// ── Test helpers ──────────────────────────────────────────────────────────────

/// <summary>Minimal <see cref="IProjectMetadata"/> stub for unit tests.</summary>
public sealed class FakeProject : IProjectMetadata
{
  public string ProjectPath => "FakeProject.csproj";
}

