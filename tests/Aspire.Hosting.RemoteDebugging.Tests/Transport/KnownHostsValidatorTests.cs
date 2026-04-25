using Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;
using FluentAssertions;

namespace Aspire.Hosting.RemoteDebugging.Tests.Transport;

[TestClass]
public class KnownHostsValidatorTests
{
  // Pre-computed test key data:
  //   Key1 (AAAA…QUFB) → fingerprint "ycecTFElO4tMaWgqfHQDEUjUete0Tsl0wmvU/kkDXFc"
  //   Key2 (AAAA…QkJC) → fingerprint "LgZpRzWEAbVvBimxTv69/UB88PD8jyOSDqfozr+iNX0"
  private const string Key1Base64 = "AAAAC3NzaC1lZDI1NTE5AAAAIEFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFB";
  private const string Key1Fingerprint = "ycecTFElO4tMaWgqfHQDEUjUete0Tsl0wmvU/kkDXFc";
  private const string Key2Fingerprint = "LgZpRzWEAbVvBimxTv69/UB88PD8jyOSDqfozr+iNX0";
  // Hashed entry for "hashed.example.com" using salt "testsalt12345678"
  private const string HashedEntry = "|1|dGVzdHNhbHQxMjM0NTY3OA==|Q/3AIfLjmcmXudyzPHmylAEC3R0=";

  // ── Helpers ──────────────────────────────────────────────────────────────

  private static string WriteKnownHosts(string contents)
  {
    var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    File.WriteAllText(path, contents);
    return path;
  }

  private static KnownHostsValidator.Result ValidateViaFile(
    string knownHostsContents, string hostname, int port, string fingerprint)
  {
    var path = WriteKnownHosts(knownHostsContents);
    try
    {
      return KnownHostsValidator.ValidateFromFile(path, hostname, port, fingerprint);
    }
    finally
    {
      File.Delete(path);
    }
  }

  // ── Plain hostname entries ────────────────────────────────────────────────

  [TestMethod]
  public void Validate_PlainHostname_MatchingKey_ReturnsTrusted()
  {
    var contents = $"myhost.example.com ssh-ed25519 {Key1Base64}\n";
    var result = ValidateViaFile(contents, "myhost.example.com", 22, Key1Fingerprint);
    result.Should().Be(KnownHostsValidator.Result.Trusted);
  }

  [TestMethod]
  public void Validate_PlainHostname_WrongKey_ReturnsMismatch()
  {
    var contents = $"myhost.example.com ssh-ed25519 {Key1Base64}\n";
    var result = ValidateViaFile(contents, "myhost.example.com", 22, Key2Fingerprint);
    result.Should().Be(KnownHostsValidator.Result.Mismatch);
  }

  [TestMethod]
  public void Validate_PlainHostname_HostNotInFile_ReturnsUnknown()
  {
    var contents = $"other.example.com ssh-ed25519 {Key1Base64}\n";
    var result = ValidateViaFile(contents, "myhost.example.com", 22, Key1Fingerprint);
    result.Should().Be(KnownHostsValidator.Result.Unknown);
  }

  // ── Port-specific entries ─────────────────────────────────────────────────

  [TestMethod]
  public void Validate_PortSpecificEntry_MatchingKeyAndPort_ReturnsTrusted()
  {
    var contents = $"[porthost.example.com]:2222 ssh-ed25519 {Key1Base64}\n";
    var result = ValidateViaFile(contents, "porthost.example.com", 2222, Key1Fingerprint);
    result.Should().Be(KnownHostsValidator.Result.Trusted);
  }

  [TestMethod]
  public void Validate_PortSpecificEntry_WrongPort_ReturnsUnknown()
  {
    var contents = $"[porthost.example.com]:2222 ssh-ed25519 {Key1Base64}\n";
    var result = ValidateViaFile(contents, "porthost.example.com", 22, Key1Fingerprint);
    result.Should().Be(KnownHostsValidator.Result.Unknown);
  }

  // ── Hashed entries ────────────────────────────────────────────────────────

  [TestMethod]
  public void Validate_HashedEntry_MatchingHost_ReturnsTrusted()
  {
    var contents = $"{HashedEntry} ssh-ed25519 {Key1Base64}\n";
    var result = ValidateViaFile(contents, "hashed.example.com", 22, Key1Fingerprint);
    result.Should().Be(KnownHostsValidator.Result.Trusted);
  }

  [TestMethod]
  public void Validate_HashedEntry_WrongHost_ReturnsUnknown()
  {
    var contents = $"{HashedEntry} ssh-ed25519 {Key1Base64}\n";
    var result = ValidateViaFile(contents, "other.example.com", 22, Key1Fingerprint);
    result.Should().Be(KnownHostsValidator.Result.Unknown);
  }

  // ── Revoked entries ───────────────────────────────────────────────────────

  [TestMethod]
  public void Validate_RevokedEntry_MatchingHost_ReturnsRevoked()
  {
    var contents = $"@revoked revoked.example.com ssh-ed25519 {Key1Base64}\n";
    var result = ValidateViaFile(contents, "revoked.example.com", 22, Key1Fingerprint);
    result.Should().Be(KnownHostsValidator.Result.Revoked);
  }

  // ── Comments and blank lines ──────────────────────────────────────────────

  [TestMethod]
  public void Validate_CommentsAndBlankLines_AreIgnored()
  {
    var contents = $"""
      # This is a comment
      
      myhost.example.com ssh-ed25519 {Key1Base64}
      """;
    var result = ValidateViaFile(contents, "myhost.example.com", 22, Key1Fingerprint);
    result.Should().Be(KnownHostsValidator.Result.Trusted);
  }

  // ── Comma-separated hostnames ─────────────────────────────────────────────

  [TestMethod]
  public void Validate_CommaSeparatedHostnames_SecondHostMatches_ReturnsTrusted()
  {
    var contents = $"alias.example.com,myhost.example.com ssh-ed25519 {Key1Base64}\n";
    var result = ValidateViaFile(contents, "myhost.example.com", 22, Key1Fingerprint);
    result.Should().Be(KnownHostsValidator.Result.Trusted);
  }

  // ── Missing file ──────────────────────────────────────────────────────────

  [TestMethod]
  public void Validate_MissingFile_ReturnsUnknown()
  {
    var nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "known_hosts");
    var result = KnownHostsValidator.ValidateFromFile(nonExistentPath, "myhost.example.com", 22, Key1Fingerprint);
    result.Should().Be(KnownHostsValidator.Result.Unknown);
  }
}
