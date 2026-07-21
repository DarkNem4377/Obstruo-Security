using System.Text.Json.Serialization;
using Obstruo.Shared.Contracts;
using Obstruo.Shared.Enums;

namespace Obstruo.Shared.Messages;

public sealed record HeartbeatMessage : IObstrouMessage
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }

    [JsonPropertyName("messageType")]
    public string MessageType => "Heartbeat";

    [JsonPropertyName("serviceOk")]
    public required bool ServiceOk { get; init; }

    [JsonPropertyName("protectionState")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required ProtectionState ProtectionState { get; init; }

    [JsonPropertyName("blockCountTotal")]
    public required long BlockCountTotal { get; init; }
}