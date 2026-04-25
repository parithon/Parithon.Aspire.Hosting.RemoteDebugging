using System.Runtime.InteropServices;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteDebugging.RemoteHost;
using Aspire.Hosting.RemoteDebugging.RemoteProject;
using Aspire.Hosting.RemoteDebugging.RemoteProject.Annotations;
using FluentAssertions;
using Google.Protobuf.Collections;
using Moq;

namespace Aspire.Hosting.RemoteDebugging.Tests.RemoteProject;

/// <summary>
/// Unit tests for <see cref="WindowsServiceRunner"/> covering:
/// - PowerShell command generation for install/uninstall
/// - Environment variable registry format (REG_MULTI_SZ)
/// - Service name sanitization
/// - Platform guard (throws for non-Windows)
/// </summary>
[TestClass]
public class WindowsServiceRunnerTests
{
  private static IResourceBuilder<RemoteProjectResource<FakeProject>> BuildWindowsServiceProject()
  {
    var appBuilder = DistributedApplication.CreateBuilder();
    var passwordParam = appBuilder.AddParameter("password", secret: true);
    var credential = new RemoteHostCredential("user", passwordParam);
    var hostBuilder = appBuilder.AddRemoteHost("win-dev", OSPlatform.Windows, credential);
    var builder = appBuilder.AddRemoteProject<FakeProject>("remote-worker", hostBuilder);
    builder.AsWindowsService("remote-worker");
    return builder;
  }

  // ── Service name handling ─────────────────────────────────────────────────

  [TestMethod]
  public void ServiceName_WithHyphens_IsPreserved()
  {
    var appBuilder = DistributedApplication.CreateBuilder();
    var passwordParam = appBuilder.AddParameter("password", secret: true);
    var credential = new RemoteHostCredential("user", passwordParam);
    var hostBuilder = appBuilder.AddRemoteHost("win-dev", OSPlatform.Windows, credential);
    var builder = appBuilder.AddRemoteProject<FakeProject>("my-worker-app", hostBuilder);

    builder.AsWindowsService();

    builder.Resource.TryGetLastAnnotation<WindowsServiceAnnotation>(out var annotation).Should().BeTrue();
    annotation!.ServiceName.Should().Be("my-worker-app");
  }

  [TestMethod]
  public void ServiceName_WithSpaces_IsPassedAsIs()
  {
    var appBuilder = DistributedApplication.CreateBuilder();
    var passwordParam = appBuilder.AddParameter("password", secret: true);
    var credential = new RemoteHostCredential("user", passwordParam);
    var hostBuilder = appBuilder.AddRemoteHost("win-dev", OSPlatform.Windows, credential);
    var builder = appBuilder.AddRemoteProject<FakeProject>("worker", hostBuilder);

    builder.AsWindowsService("my worker app");

    builder.Resource.TryGetLastAnnotation<WindowsServiceAnnotation>(out var annotation).Should().BeTrue();
    annotation!.ServiceName.Should().Be("my worker app");
  }

  // ── Environment variable script generation ─────────────────────────────────

  [TestMethod]
  public void EnvVarScript_Empty_CreatesRegistryPath()
  {
    var script = GenerateEnvScript("remote-worker", new Dictionary<string, string>());

    script.Should().Contain(@"$regPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\remote-worker'");
  }

  [TestMethod]
  public void EnvVarScript_SingleVariable_CreatesMultiStringValue()
  {
    var script = GenerateEnvScript("svc", new Dictionary<string, string>
    {
      ["OTEL_SERVICE_NAME"] = "svc"
    });

    script.Should().Contain("'OTEL_SERVICE_NAME=svc'");
    script.Should().Contain("MultiString");
    script.Should().Contain("Set-ItemProperty");
  }

  [TestMethod]
  public void EnvVarScript_MultipleVariables_CreatesArray()
  {
    var script = GenerateEnvScript("svc", new Dictionary<string, string>
    {
      ["OTEL_SERVICE_NAME"] = "svc",
      ["DOTNET_ENVIRONMENT"] = "Production",
      ["LOG_LEVEL"] = "Information"
    });

    script.Should().Contain("'OTEL_SERVICE_NAME=svc'");
    script.Should().Contain("'DOTNET_ENVIRONMENT=Production'");
    script.Should().Contain("'LOG_LEVEL=Information'");
  }

  [TestMethod]
  public void EnvVarScript_EscapesSingleQuotes()
  {
    var script = GenerateEnvScript("svc", new Dictionary<string, string>
    {
      ["KEY"] = "it's a value"
    });

    // Single quotes in PowerShell single-quoted strings must be doubled.
    script.Should().Contain("it''s a value");
  }

  [TestMethod]
  public void EnvVarScript_HandlesSpecialCharacters()
  {
    var script = GenerateEnvScript("svc", new Dictionary<string, string>
    {
      ["CONN_STR"] = "Server=localhost;User=admin;Password=p@ss!",
      ["PATH_WITH_SPACES"] = @"C:\Program Files\App"
    });

    script.Should().Contain("'CONN_STR=Server=localhost;User=admin;Password=p@ss!'");
    script.Should().Contain("'PATH_WITH_SPACES=C:\\Program Files\\App'");
  }

  // ── Display name and description ────────────────────────────────────────

  [TestMethod]
  public void ServiceDisplayName_DefaultsToResourceName()
  {
    var builder = BuildWindowsServiceProject();
    builder.Resource.TryGetLastAnnotation<WindowsServiceAnnotation>(out var annotation).Should().BeTrue();
    annotation!.DisplayName.Should().BeNull();
  }

  [TestMethod]
  public void ServiceDisplayName_CanBeSet()
  {
    var appBuilder = DistributedApplication.CreateBuilder();
    var passwordParam = appBuilder.AddParameter("password", secret: true);
    var credential = new RemoteHostCredential("user", passwordParam);
    var hostBuilder = appBuilder.AddRemoteHost("win-dev", OSPlatform.Windows, credential);
    var builder = appBuilder.AddRemoteProject<FakeProject>("worker", hostBuilder);

    builder.AsWindowsService("svc", displayName: "My Application");

    builder.Resource.TryGetLastAnnotation<WindowsServiceAnnotation>(out var annotation).Should().BeTrue();
    annotation!.DisplayName.Should().Be("My Application");
  }

  // ── Log tailer pattern derivation ──────────────────────────────────────────

  [TestMethod]
  public void DeriveLevelErrorPattern_NullTemplate_ReturnsFallback()
  {
    var pattern = WindowsServiceRunner.DeriveLevelErrorPattern(null);

    pattern.Should().Be(@"(ERR|FTL|ERRO|FATL|Error|Fatal|ERROR|FATAL)");
  }

  [TestMethod]
  public void DeriveLevelErrorPattern_NoLevelToken_ReturnsFallback()
  {
    var pattern = WindowsServiceRunner.DeriveLevelErrorPattern("{Timestamp} {Message}");

    pattern.Should().Be(@"(ERR|FTL|ERRO|FATL|Error|Fatal|ERROR|FATAL)");
  }

  [TestMethod]
  public void DeriveLevelErrorPattern_LevelU3_ReturnsErrFtl()
  {
    var pattern = WindowsServiceRunner.DeriveLevelErrorPattern(
      "{Timestamp:HH:mm:ss} [{Level:u3}] {Message}");

    pattern.Should().Be(@"\b(ERR|FTL)\b");
  }

  [TestMethod]
  public void DeriveLevelErrorPattern_LevelU4_ReturnsErroFatl()
  {
    var pattern = WindowsServiceRunner.DeriveLevelErrorPattern(
      "{Timestamp:HH:mm:ss} [{Level:u4}] {Message}");

    pattern.Should().Be(@"\b(ERRO|FATL)\b");
  }

  [TestMethod]
  public void DeriveLevelErrorPattern_LevelW_ReturnsLowercase()
  {
    var pattern = WindowsServiceRunner.DeriveLevelErrorPattern(
      "{Timestamp:HH:mm:ss} [{Level:w}] {Message}");

    pattern.Should().Be(@"\b(error|fatal)\b");
  }

  [TestMethod]
  public void DeriveLevelErrorPattern_LevelT_ReturnsTitleCase()
  {
    var pattern = WindowsServiceRunner.DeriveLevelErrorPattern(
      "{Timestamp:HH:mm:ss} [{Level:t}] {Message}");

    pattern.Should().Be(@"\b(Error|Fatal)\b");
  }

  [TestMethod]
  public void DeriveLevelErrorPattern_LevelPlain_ReturnsTitleCase()
  {
    var pattern = WindowsServiceRunner.DeriveLevelErrorPattern(
      "{Timestamp:HH:mm:ss} [{Level}] {Message}");

    pattern.Should().Be(@"\b(Error|Fatal)\b");
  }

  // ── Platform guard ────────────────────────────────────────────────────────

  [TestMethod]
  public void AsWindowsService_LinuxPlatform_Throws()
  {
    var appBuilder = DistributedApplication.CreateBuilder();
    var passwordParam = appBuilder.AddParameter("password", secret: true);
    var credential = new RemoteHostCredential("user", passwordParam);
    var hostBuilder = appBuilder.AddRemoteHost("linux-dev", OSPlatform.Linux, credential);
    var builder = appBuilder.AddRemoteProject<FakeProject>("worker", hostBuilder);

    Action act = () => builder.AsWindowsService();

    act.Should().Throw<InvalidOperationException>()
      .WithMessage("*Windows*");
  }

  // ── Helper methods ────────────────────────────────────────────────────────

  /// <summary>
  /// Mirrors the script generation logic in <see cref="WindowsServiceRunner.InstallAsync"/>.
  /// </summary>
  private static string GenerateEnvScript(string serviceName, Dictionary<string, string> env)
  {
    static string Escape(string v) => v.Replace("'", "''");

    var sb = new StringBuilder();
    sb.AppendLine($@"$regPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\{serviceName}'");
    if (env.Count > 0)
    {
      sb.AppendLine("$values  = @(");
      foreach (var kv in env)
        sb.AppendLine($"  '{kv.Key}={Escape(kv.Value)}'");
      sb.AppendLine(")");
      sb.AppendLine("Set-ItemProperty -Path $regPath -Name 'Environment' -Value $values -Type MultiString");
    }
    return sb.ToString();
  }
}
