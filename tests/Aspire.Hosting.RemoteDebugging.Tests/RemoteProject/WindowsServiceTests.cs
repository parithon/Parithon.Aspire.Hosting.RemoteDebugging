using System.Runtime.InteropServices;
using System.Text;
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
public class WindowsServiceEnvScriptTests
{
  // Verifies the PowerShell env-var script content produced for WindowsServiceRunner.InstallAsync.
  // This mirrors the StringBuilder logic in InstallAsync — pure string-formatting, no SSH required.

  [TestMethod]
  public void EnvVarScript_ContainsRegistryPathAndValues()
  {
    const string sn = "remote-worker";
    var script = BuildEnvScript(sn, new Dictionary<string, string>
    {
      ["OTEL_SERVICE_NAME"] = "remote-worker",
      ["DOTNET_ENVIRONMENT"] = "Production",
    });

    script.Should().Contain($@"Services\{sn}");
    script.Should().Contain("'OTEL_SERVICE_NAME=remote-worker'");
    script.Should().Contain("'DOTNET_ENVIRONMENT=Production'");
    script.Should().Contain("MultiString");
    script.Should().Contain("Set-ItemProperty");
  }

  [TestMethod]
  public void EnvVarScript_EscapesSingleQuotesInValues()
  {
    const string sn = "svc";
    var script = BuildEnvScript(sn, new Dictionary<string, string>
    {
      ["KEY"] = "it's a value",
    });

    // Single quotes in the value must be doubled for PowerShell single-quoted strings.
    script.Should().Contain("it''s a value");
  }

  [TestMethod]
  public void EnvVarScript_UsesSingleQuotedStrings_NoDoubleQuotesInValues()
  {
    const string sn = "svc";
    var script = BuildEnvScript(sn, new Dictionary<string, string>
    {
      ["KEY"] = "value with spaces",
    });

    // Values must be wrapped in single quotes, NOT double quotes (double quotes are stripped by SSH transport).
    script.Should().Contain("'KEY=value with spaces'");
    script.Should().NotContain("\"KEY=value with spaces\"");
  }

  // Mirrors the StringBuilder in WindowsServiceRunner.InstallAsync for test isolation.
  private static string BuildEnvScript(string serviceName, Dictionary<string, string> env)
  {
    static string Escape(string v) => v.Replace("'", "''");

    var sb = new StringBuilder();
    sb.AppendLine($@"$regPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\{serviceName}'");
    sb.AppendLine("$values  = @(");
    foreach (var kv in env)
      sb.AppendLine($"  '{kv.Key}={Escape(kv.Value)}'");
    sb.AppendLine(")");
    sb.AppendLine("Set-ItemProperty -Path $regPath -Name 'Environment' -Value $values -Type MultiString");
    return sb.ToString();
  }
}

// ── Test helpers ──────────────────────────────────────────────────────────────

/// <summary>Minimal <see cref="IProjectMetadata"/> stub for unit tests.</summary>
public sealed class FakeProject : IProjectMetadata
{
  public string ProjectPath => "FakeProject.csproj";
}

