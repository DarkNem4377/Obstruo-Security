using System.Text.Json.Serialization;
using Obstruo.Shared.Contracts;
using Obstruo.Shared.Enums;

namespace Obstruo.Shared.Messages;

public sealed record AlertMessage : IObstrouMessage
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }

    [JsonPropertyName("messageType")]
    public string MessageType => "Alert";

    [JsonPropertyName("alertType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required AlertType AlertType { get; init; }

    [JsonPropertyName("severity")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required Severity Severity { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}