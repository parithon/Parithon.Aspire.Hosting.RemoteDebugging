using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting.RemoteDebugging.Sidecar.Daemon;

internal enum MessageType
{
  // Requests (client → daemon)
  Start,
  Stop,
  Status,
  Logs,

  // Responses (daemon → client)
  Started,
  Stopped,
  StatusResult,
  LogLine,
  Error,
}

internal sealed record SidecarMessage(
  [property: JsonConverter(typeof(JsonStringEnumConverter))] MessageType Type)
{
  // Start request
  public string? Project { get; init; }
  public string? Path { get; init; }
  public string? Executable { get; init; }

  // Logs request
  public long From { get; init; }

  // Started response
  public int Pid { get; init; }

  // StatusResult response
  public bool Running { get; init; }
  public int ExitCode { get; init; }

  // LogLine response
  public string? Line { get; init; }
  public long Offset { get; init; }

  // Error response
  public string? Message { get; init; }

  public string Serialize() => JsonSerializer.Serialize(this);

  public static SidecarMessage? Deserialize(string json) =>
    JsonSerializer.Deserialize<SidecarMessage>(json);
}
