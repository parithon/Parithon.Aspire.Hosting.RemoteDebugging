using System.Security.Cryptography;
using System.Text;

namespace Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;

/// <summary>
/// Validates SSH host keys against the user's <c>~/.ssh/known_hosts</c> file,
/// mirroring OpenSSH's host-key verification semantics.
/// </summary>
internal static class KnownHostsValidator
{
  internal enum Result { Trusted, Unknown, Mismatch, Revoked }

  /// <summary>
  /// Validates <paramref name="sha256Fingerprint"/> against all entries in the
  /// user's <c>~/.ssh/known_hosts</c> that match <paramref name="hostname"/> and
  /// <paramref name="port"/>.
  /// </summary>
  internal static Result Validate(string hostname, int port, string sha256Fingerprint)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
    if (port < 1 || port > 65535)
      throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 1 and 65535.");
    ArgumentException.ThrowIfNullOrWhiteSpace(sha256Fingerprint);

    var knownHostsPath = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
      ".ssh", "known_hosts");

    return ValidateFromFile(knownHostsPath, hostname, port, sha256Fingerprint);
  }

  /// <summary>
  /// Validates against a specific <paramref name="filePath"/>; exposed for unit testing.
  /// </summary>
  internal static Result ValidateFromFile(string filePath, string hostname, int port, string sha256Fingerprint)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
    if (port < 1 || port > 65535)
      throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 1 and 65535.");
    ArgumentException.ThrowIfNullOrWhiteSpace(sha256Fingerprint);

    if (!File.Exists(filePath))
      return Result.Unknown;

    bool anyHostMatch = false;

    foreach (var line in File.ReadLines(filePath))
    {
      if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
        continue;

      // Detect @revoked / @cert-authority markers
      bool isRevoked = false;
      var remainder = line;
      if (remainder.StartsWith('@'))
      {
        var space = remainder.IndexOf(' ');
        if (space < 0) continue;
        var marker = remainder[..space];
        if (marker.Equals("@revoked", StringComparison.OrdinalIgnoreCase))
          isRevoked = true;
        else if (marker.Equals("@cert-authority", StringComparison.OrdinalIgnoreCase))
          continue; // certificate authority entries are out of scope
        else
          continue;
        remainder = remainder[(space + 1)..];
      }

      // Split into: hostsField  keyType  base64Key  [comment]
      var parts = remainder.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length < 3) continue;

      var hostsField = parts[0];
      var keyBase64 = parts[2].Split(' ')[0].Trim(); // strip inline comments

      if (!HostFieldMatches(hostsField, hostname, port))
        continue;

      anyHostMatch = true;

      if (isRevoked)
        return Result.Revoked;

      // Compute SHA-256 fingerprint of the stored key and compare.
      if (FingerprintFromBase64Key(keyBase64) is string knownFingerprint
          && string.Equals(knownFingerprint, sha256Fingerprint, StringComparison.Ordinal))
        return Result.Trusted;
    }

    return anyHostMatch ? Result.Mismatch : Result.Unknown;
  }

  // ── Helpers ──────────────────────────────────────────────────────────────

  private static bool HostFieldMatches(string hostsField, string hostname, int port)
  {
    // Build candidate patterns for this hostname + port.
    // OpenSSH stores non-22 ports as "[hostname]:port".
    var candidates = port == 22
      ? [hostname]
      : new[] { $"[{hostname}]:{port}", hostname };

    foreach (var entry in hostsField.Split(','))
    {
      var e = entry.Trim();
      if (string.IsNullOrEmpty(e) || e.StartsWith('!'))
        continue; // negation entries — skip (uncommon; treat as no-match)

      if (e.StartsWith("|1|"))
      {
        if (candidates.Any(c => MatchesHashedEntry(e, c)))
          return true;
      }
      else
      {
        if (candidates.Any(c => string.Equals(e, c, StringComparison.OrdinalIgnoreCase)))
          return true;
      }
    }

    return false;
  }

  /// <summary>
  /// Checks whether <paramref name="candidate"/> matches a hashed known_hosts entry
  /// of the form <c>|1|&lt;base64-salt&gt;|&lt;base64-hmac-sha1&gt;</c>.
  /// </summary>
  private static bool MatchesHashedEntry(string hashedEntry, string candidate)
  {
    // Format: |1|base64salt|base64hmac
    var segments = hashedEntry.Split('|');
    if (segments.Length != 4) return false; // ["", "1", salt, hmac]

    try
    {
      var salt = Convert.FromBase64String(segments[2]);
      var expectedHmac = Convert.FromBase64String(segments[3]);
      using var hmac = new HMACSHA1(salt);
      var computed = hmac.ComputeHash(Encoding.UTF8.GetBytes(candidate));
      return computed.SequenceEqual(expectedHmac);
    }
    catch (FormatException)
    {
      // Invalid base64 encoding in known_hosts; not a match.
      return false;
    }
  }

  /// <summary>
  /// Decodes a base64 public key blob from <c>known_hosts</c> and returns its
  /// non-padded base64 SHA-256 fingerprint — the same format as OpenSSH and
  /// SSH.NET's <c>HostKeyEventArgs.FingerPrintSHA256</c>.
  /// </summary>
  private static string? FingerprintFromBase64Key(string base64Key)
  {
    try
    {
      var keyBytes = Convert.FromBase64String(base64Key);
      var hash = SHA256.HashData(keyBytes);
      // OpenSSH and SSH.NET both strip base64 padding from fingerprints.
      return Convert.ToBase64String(hash).TrimEnd('=');
    }
    catch (FormatException)
    {
      // Invalid base64 encoding; not a valid key.
      return null;
    }
  }
}
