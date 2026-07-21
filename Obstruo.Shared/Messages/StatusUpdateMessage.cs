using System.Text.Json.Serialization;
using Obstruo.Shared.Contracts;
using Obstruo.Shared.Enums;

namespace Obstruo.Shared.Messages;

public sealed record StatusUpdateMessage : IObstrouMessage
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }

    [JsonPropertyName("messageType")]
    public string MessageType => "StatusUpdate";

    [JsonPropertyName("protectionState")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required ProtectionState ProtectionState { get; init; }

    [JsonPropertyName("uptimeSeconds")]
    public required long UptimeSeconds { get; init; }

    [JsonPropertyName("blockCount")]
    public required int BlockCount { get; init; }

    [JsonPropertyName("threatLevel")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required ThreatLevel ThreatLevel { get; init; }

    /// <summary>False while every configured upstream DNS server is unreachable
    /// (the proxy is up but cannot forward, so lookups fail closed).</summary>
    [JsonPropertyName("upstreamHealthy")]
    public bool UpstreamHealthy { get; init; } = true;

    /// <summary>Live rule counts per enabled category, ordered largest first.
    /// Null from services older than 1.0.3.</summary>
    [JsonPropertyName("ruleCounts")]
    public Dictionary<string, int>? RuleCounts { get; init; }
}