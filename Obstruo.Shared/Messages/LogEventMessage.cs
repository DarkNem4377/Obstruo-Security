using System.Text.Json.Serialization;
using Obstruo.Shared.Contracts;
using Obstruo.Shared.Enums;

namespace Obstruo.Shared.Messages;

public sealed record LogEventMessage : IObstrouMessage
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }

    [JsonPropertyName("messageType")]
    public string MessageType => "LogEvent";

    [JsonPropertyName("domain")]
    public required string Domain { get; init; }

    [JsonPropertyName("category")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required BlockCategory Category { get; init; }

    [JsonPropertyName("severity")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required Severity Severity { get; init; }

    [JsonPropertyName("deviceName")]
    public required string DeviceName { get; init; }

    [JsonPropertyName("sourceProcess")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceProcess { get; init; }

    // Note: a "geo" field used to live here but was never populated. Removed from
    // the contract to stop advertising a dead field. The BlockedEvents.geo column
    // is left in place (harmless, always NULL) to avoid a drop-column migration.

    [JsonPropertyName("mitre")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mitre { get; init; }

    [JsonPropertyName("incidentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IncidentId { get; init; }
}