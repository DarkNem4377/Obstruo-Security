using System.Text.Json;
using System.Text.Json.Serialization;
using Obstruo.Shared.Messages;

namespace Obstruo.Shared.Contracts;

/// <summary>
/// Handles serialization and deserialization of all IPC messages.
///
/// Wire framing: newline-delimited JSON (NDJSON).
/// Every serialized message is a single line with no internal newlines.
/// The sender appends MessageDelimiter after every message.
/// The receiver reads with ReadLineAsync() — one line = one complete message.
/// </summary>
public static class IpcSerializer
{
    /// <summary>Appended by the sender after every message.</summary>
    public const string MessageDelimiter = "\n";

    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    // ─── Outbound (Service → UI and UI → Service) ─────────────────────────────

    /// <summary>
    /// Serializes any message to a JSON string.
    /// Append MessageDelimiter before writing to the pipe.
    /// </summary>
    public static string Serialize<T>(T message) where T : IObstrouMessage
        => JsonSerializer.Serialize(message, _options);

    // ─── Inbound (Service side — only expects CommandMessage) ─────────────────

    /// <summary>
    /// Deserializes a raw line from the pipe into a CommandMessage.
    /// Returns false if the JSON is malformed or not a Command.
    /// </summary>
    public static bool TryDeserializeCommand(string json, out CommandMessage? command)
    {
        try
        {
            command = JsonSerializer.Deserialize<CommandMessage>(json, _options);
            return command is not null;
        }
        catch
        {
            command = null;
            return false;
        }
    }

    // ─── Inbound (UI side — handles mixed inbound stream) ─────────────────────

    /// <summary>
    /// Peeks at the messageType field and deserializes to the correct concrete type.
    /// Used by the UI to process the mixed stream coming from the service.
    /// Returns false if the type is unknown or the JSON is malformed.
    /// </summary>
    public static bool TryDeserialize(string json, out IObstrouMessage? message)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("messageType", out var typeProp))
            {
                message = null;
                return false;
            }

            message = typeProp.GetString() switch
            {
                "LogEvent" => JsonSerializer.Deserialize<LogEventMessage>(json, _options),
                "StatusUpdate" => JsonSerializer.Deserialize<StatusUpdateMessage>(json, _options),
                "MetricsUpdate" => JsonSerializer.Deserialize<MetricsUpdateMessage>(json, _options),
                "CommandResponse" => JsonSerializer.Deserialize<CommandResponseMessage>(json, _options),
                "Alert" => JsonSerializer.Deserialize<AlertMessage>(json, _options),
                "Heartbeat" => JsonSerializer.Deserialize<HeartbeatMessage>(json, _options),
                _ => null
            };

            return message is not null;
        }
        catch
        {
            message = null;
            return false;
        }
    }
}