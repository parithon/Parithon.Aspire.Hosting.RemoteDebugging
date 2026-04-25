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
  public void AsWindowsService_DefaultDisplayNameAndDescription_AreNullWhenNotExplicitlySet()
  {
    var appBuilder = DistributedApplication.CreateBuilder();
    var builder = BuildProjectOn(appBuilder, "win-dev", OSPlatform.Windows, projectName: "my-worker-app");

    builder.AsWindowsService();

    // DisplayName and Description are intentionally null when not explicitly provided.
    // The runner resolves defaults (resource name / "Aspire remote project: ...") at use-time.
    builder.Resource.TryGetLastAnnotation<WindowsServiceAnnotation>(out var annotation).Should().BeTrue();
    annotation!.DisplayName.Should().BeNull();
    annotation.Description.Should().BeNull();
  }

  [TestMethod]
  public void AsWindowsService_ServiceNameWithSpaces_ThrowsArgumentException()
  {
    var appBuilder = DistributedApplication.CreateBuilder();
    var builder = BuildProjectOn(appBuilder, "win-dev", OSPlatform.Windows);

    // Service names with spaces would allow sc.exe command injection; they must be rejected.
    var act = () => builder.AsWindowsService(serviceName: "my worker app");

    act.Should().Throw<ArgumentException>()
      .WithMessage("*invalid characters*");
  }

  [TestMethod]
  public void AsWindowsService_DisplayNameWithDoubleQuote_ThrowsArgumentException()
  {
    var appBuilder = DistributedApplication.CreateBuilder();
    var builder = BuildProjectOn(appBuilder, "win-dev", OSPlatform.Windows);

    var act = () => builder.AsWindowsService(displayName: "My \"Service\"");

    act.Should().Throw<ArgumentException>()
      .WithMessage("*invalid characters*");
  }

  [TestMethod]
  public void AsWindowsService_DescriptionWithNewLine_ThrowsArgumentException()
  {
    var appBuilder = DistributedApplication.CreateBuilder();
    var builder = BuildProjectOn(appBuilder, "win-dev", OSPlatform.Windows);

    var act = () => builder.AsWindowsService(description: "line1\nline2");

    act.Should().Throw<ArgumentException>()
      .WithMessage("*invalid characters*");
  }

  [TestMethod]
  public void AsWindowsService_DescriptionWithShellMetacharacters_ThrowsArgumentException()
  {
    var appBuilder = DistributedApplication.CreateBuilder();
    var builder = BuildProjectOn(appBuilder, "win-dev", OSPlatform.Windows);

    var act = () => builder.AsWindowsService(description: "safe & sc.exe stop spooler");

    act.Should().Throw<ArgumentException>()
      .WithMessage("*invalid characters*");
  }

  [TestMethod]
  public void AsWindowsService_DisplayNameWithShellMetacharacters_ThrowsArgumentException()
  {
    var appBuilder = DistributedApplication.CreateBuilder();
    var builder = BuildProjectOn(appBuilder, "win-dev", OSPlatform.Windows);

    var act = () => builder.AsWindowsService(displayName: "service & sc.exe stop spooler");

    act.Should().Throw<ArgumentException>()
      .WithMessage("*invalid characters*");
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

  [TestMethod]
  public void WithEnvironment_KeyWithShellMetacharacters_ThrowsArgumentException()
  {
    var appBuilder = DistributedApplication.CreateBuilder();
    var builder = BuildProjectOn(appBuilder, "win-dev", OSPlatform.Windows);

    var act = () => builder.WithEnvironment("BAD&KEY", "value");

    act.Should().Throw<ArgumentException>()
      .WithParameterName("key");
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
  public void EnvVarScript_EscapesSingleQuotesInKeys()
  {
    const string sn = "svc";
    var script = BuildEnvScript(sn, new Dictionary<string, string>
    {
      ["KEY'PART"] = "value",
    });

    // Single quotes in keys must also be doubled for PowerShell single-quoted strings.
    script.Should().Contain("KEY''PART=value");
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
      sb.AppendLine($"  '{Escape(kv.Key)}={Escape(kv.Value)}'");
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

// ── LoggingSupportAnnotation tests ────────────────────────────────────────────

[TestClass]
public class LoggingSupportAnnotationTests
{
  [TestMethod]
  public void LoggingSupportAnnotation_Properties_AreSet()
  {
    var annotation = new LoggingSupportAnnotation(@"C:\Logs\app\app.log")
    {
      OutputTemplate = "{Timestamp} [{Level:u3}] {Message}",
    };

    annotation.LogFilePath.Should().Be(@"C:\Logs\app\app.log");
    annotation.OutputTemplate.Should().Be("{Timestamp} [{Level:u3}] {Message}");
  }

  [TestMethod]
  public void LoggingSupportAnnotation_OutputTemplate_DefaultsToNull()
  {
    var annotation = new LoggingSupportAnnotation(@"C:\Logs\app\app.log");

    annotation.OutputTemplate.Should().BeNull();
  }
}

// ── WithLoggingSupport extension tests ───────────────────────────────────────

[TestClass]
public class WithLoggingSupportExtensionTests
{
  private static IResourceBuilder<RemoteProjectResource<FakeProject>> BuildWindowsServiceProject()
  {
    var appBuilder    = DistributedApplication.CreateBuilder();
    var passwordParam = appBuilder.AddParameter("password", secret: true);
    var credential    = new RemoteHostCredential("user", passwordParam);
    var hostBuilder   = appBuilder.AddRemoteHost("win-dev", OSPlatform.Windows, credential);
    var builder       = appBuilder.AddRemoteProject<FakeProject>("remote-worker", hostBuilder);
    builder.AsWindowsService("remoteworker");
    return builder;
  }

  [TestMethod]
  public void WithLoggingSupport_WithoutAsWindowsService_ThrowsInvalidOperationException()
  {
    var appBuilder    = DistributedApplication.CreateBuilder();
    var passwordParam = appBuilder.AddParameter("password", secret: true);
    var credential    = new RemoteHostCredential("user", passwordParam);
    var hostBuilder   = appBuilder.AddRemoteHost("win-dev", OSPlatform.Windows, credential);
    var builder       = appBuilder.AddRemoteProject<FakeProject>("remote-worker", hostBuilder);

    var act = () => builder.WithLoggingSupport(@"C:\Logs\app\app.log");

    act.Should().Throw<InvalidOperationException>()
      .WithMessage("*WithLoggingSupport()*AsWindowsService()*");
  }

  [TestMethod]
  public void WithLoggingSupport_WithAsWindowsService_AddsLoggingSupportAnnotation()
  {
    var builder = BuildWindowsServiceProject();

    builder.WithLoggingSupport(@"C:\Logs\app\app.log", "{Timestamp} [{Level:u3}] {Message}");

    builder.Resource.TryGetLastAnnotation<LoggingSupportAnnotation>(out var annotation).Should().BeTrue();
    annotation!.LogFilePath.Should().Be(@"C:\Logs\app\app.log");
    annotation.OutputTemplate.Should().Be("{Timestamp} [{Level:u3}] {Message}");
  }

  [TestMethod]
  public void WithLoggingSupport_NoOutputTemplate_AnnotationOutputTemplateIsNull()
  {
    var builder = BuildWindowsServiceProject();

    builder.WithLoggingSupport(@"C:\Logs\app\app.log");

    builder.Resource.TryGetLastAnnotation<LoggingSupportAnnotation>(out var annotation).Should().BeTrue();
    annotation!.OutputTemplate.Should().BeNull();
  }

  [TestMethod]
  public void WithLoggingSupport_ReturnsOriginalBuilder()
  {
    var builder = BuildWindowsServiceProject();

    var result = builder.WithLoggingSupport(@"C:\Logs\app\app.log");

    result.Should().BeSameAs(builder);
  }
}

// ── Log tailer script tests ───────────────────────────────────────────────────

[TestClass]
public class LogTailerScriptTests
{
  [TestMethod]
  public void DeriveLevelErrorPattern_NullTemplate_ReturnsFallbackPattern()
  {
    var pattern = WindowsServiceRunner.DeriveLevelErrorPattern(null);

    pattern.Should().Be(@"(ERR|FTL|ERRO|FATL|Error|Fatal|ERROR|FATAL)");
  }

  [TestMethod]
  public void DeriveLevelErrorPattern_NoLevelToken_ReturnsFallbackPattern()
  {
    var pattern = WindowsServiceRunner.DeriveLevelErrorPattern("{Timestamp} {Message}");

    pattern.Should().Be(@"(ERR|FTL|ERRO|FATL|Error|Fatal|ERROR|FATAL)");
  }

  [TestMethod]
  public void DeriveLevelErrorPattern_U3Format_ReturnsErrFtlPattern()
  {
    var pattern = WindowsServiceRunner.DeriveLevelErrorPattern(
      "{Timestamp:HH:mm:ss} [{Level:u3}] {Message}");

    pattern.Should().Be(@"\b(ERR|FTL)\b");
  }

  [TestMethod]
  public void DeriveLevelErrorPattern_U4Format_ReturnsErroFatlPattern()
  {
    var pattern = WindowsServiceRunner.DeriveLevelErrorPattern(
      "{Timestamp:HH:mm:ss} [{Level:u4}] {Message}");

    pattern.Should().Be(@"\b(ERRO|FATL)\b");
  }

  [TestMethod]
  public void DeriveLevelErrorPattern_WFormat_ReturnsLowercasePattern()
  {
    var pattern = WindowsServiceRunner.DeriveLevelErrorPattern(
      "{Timestamp:HH:mm:ss} [{Level:w}] {Message}");

    pattern.Should().Be(@"\b(error|fatal)\b");
  }

  [TestMethod]
  public void DeriveLevelErrorPattern_UnknownFormat_ReturnsTitleCasePattern()
  {
    var pattern = WindowsServiceRunner.DeriveLevelErrorPattern(
      "{Timestamp:HH:mm:ss} [{Level:t}] {Message}");

    pattern.Should().Be(@"\b(Error|Fatal)\b");
  }

  [TestMethod]
  public void DeriveLevelErrorPattern_LevelWithNoFormat_ReturnsTitleCasePattern()
  {
    var pattern = WindowsServiceRunner.DeriveLevelErrorPattern(
      "{Timestamp:HH:mm:ss} [{Level}] {Message}");

    pattern.Should().Be(@"\b(Error|Fatal)\b");
  }
}

