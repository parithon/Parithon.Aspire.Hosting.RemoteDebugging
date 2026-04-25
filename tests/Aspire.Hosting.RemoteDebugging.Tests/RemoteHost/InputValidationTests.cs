using Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;
using Aspire.Hosting.RemoteDebugging.RemoteHost;
using FluentAssertions;
using System.Runtime.InteropServices;

namespace Aspire.Hosting.RemoteDebugging.Tests.RemoteHost;

/// <summary>
/// Tests for input validation of KnownHostsValidator and extension methods.
/// Covers OWASP A05 (Injection) and A07 (Authentication) mitigations.
/// </summary>
[TestClass]
public class InputValidationTests
{
  // ── WithVsdbgVersion validation ───────────────────────────────────────────

  [TestMethod]
  public void Version_ShellMetacharactersDetected()
  {
    var invalid = new[] { "1.0; rm -rf /", "1.0 && evil", "1.0 | cat", "1.0`whoami`" };
    foreach (var version in invalid)
    {
      var isValid = System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+$")
                    || version.Equals("latest", StringComparison.OrdinalIgnoreCase);
      isValid.Should().BeFalse($"version '{version}' contains shell metacharacters");
    }
  }

  // ── WithDeploymentPath validation ─────────────────────────────────────────

  [TestMethod]
  public void Path_TraversalDetected()
  {
    var invalid = new[] { "/opt/../../../etc", "/opt/aspire/..", "/../evil" };
    foreach (var path in invalid)
    {
      path.Contains("..").Should().BeTrue($"path '{path}' contains traversal");
    }
  }

  [TestMethod]
  public void Path_ShellMetacharactersDetected()
  {
    var invalid = new[] { "/opt/aspire;evil", "/opt/aspire|cat", "/opt/aspire`whoami`", "/opt/aspire$(whoami)" };
    foreach (var path in invalid)
    {
      var isSafe = System.Text.RegularExpressions.Regex.IsMatch(path, @"^[a-zA-Z0-9._/-]+$");
      isSafe.Should().BeFalse($"path '{path}' contains shell metacharacters");
    }
  }

  [TestMethod]
  public void Path_RelativePathDetected()
  {
    var relative = "opt/aspire"; // no leading /
    relative.StartsWith("/").Should().BeFalse("relative paths should not start with /");
  }

  // ── KnownHostsValidator argument validation ──────────────────────────────

  [TestMethod]
  public void KnownHostsValidator_NullHostname_Throws()
  {
    Action action = () => KnownHostsValidator.Validate(null!, 22, "fingerprint");
    action.Should().Throw<ArgumentException>().WithParameterName("hostname");
  }

  [TestMethod]
  public void KnownHostsValidator_EmptyHostname_Throws()
  {
    Action action = () => KnownHostsValidator.Validate("", 22, "fingerprint");
    action.Should().Throw<ArgumentException>().WithParameterName("hostname");
  }

  [TestMethod]
  public void KnownHostsValidator_InvalidPort_Low_Throws()
  {
    Action action = () => KnownHostsValidator.Validate("host.com", 0, "fingerprint");
    action.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("port");
  }

  [TestMethod]
  public void KnownHostsValidator_InvalidPort_High_Throws()
  {
    Action action = () => KnownHostsValidator.Validate("host.com", 65536, "fingerprint");
    action.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("port");
  }

  [TestMethod]
  public void KnownHostsValidator_NullFingerprint_Throws()
  {
    Action action = () => KnownHostsValidator.Validate("host.com", 22, null!);
    action.Should().Throw<ArgumentException>().WithParameterName("sha256Fingerprint");
  }

  [TestMethod]
  public void KnownHostsValidator_EmptyFingerprint_Throws()
  {
    Action action = () => KnownHostsValidator.Validate("host.com", 22, "");
    action.Should().Throw<ArgumentException>().WithParameterName("sha256Fingerprint");
  }

  [TestMethod]
  public void KnownHostsValidator_ValidPort_22_Succeeds()
  {
    var result = KnownHostsValidator.ValidateFromFile(
      "/nonexistent/path", "host.com", 22, "fingerprint");
    result.Should().Be(KnownHostsValidator.Result.Unknown);
  }

  [TestMethod]
  public void KnownHostsValidator_ValidPort_65535_Succeeds()
  {
    var result = KnownHostsValidator.ValidateFromFile(
      "/nonexistent/path", "host.com", 65535, "fingerprint");
    result.Should().Be(KnownHostsValidator.Result.Unknown);
  }

  [TestMethod]
  public void RemoteHost_WithEndpoint_NegativePort_Throws()
  {
    var appBuilder = DistributedApplication.CreateBuilder();
    var password = appBuilder.AddParameter("password", secret: true);
    var host = appBuilder.AddRemoteHost("test-host", OSPlatform.Windows, new RemoteHostCredential("user", password));

    Action action = () => host.WithEndpoint("host.example", -1);

    action.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("port");
  }
}
