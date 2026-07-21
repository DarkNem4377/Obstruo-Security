using System.Text.Json.Serialization;
using Obstruo.Shared.Contracts;
using Obstruo.Shared.Enums;

namespace Obstruo.Shared.Messages;

public sealed record CommandResponseMessage : IObstrouMessage
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }

    [JsonPropertyName("messageType")]
    public string MessageType => "CommandResponse";

    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    [JsonPropertyName("updatedState")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProtectionState? UpdatedState { get; init; }

    /// <summary>
    /// Command-specific result data, serialized as a JSON string.
    /// Null for commands that return no data.
    ///
    /// GetBlocklist → JSON of BlocklistSnapshot:
    /// {
    ///   "categories": [ { "name": "Adult", "count": 171 }, ... ],
    ///   "domains":    [ { "domain": "example.com", "category": "Adult", "source": "obstruo-builtin" }, ... ]
    /// }
    /// Domains are sent in PLAIN TEXT — masking is a UI display concern only.
    /// </summary>
    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; init; }
}