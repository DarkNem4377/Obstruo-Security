using System.Text.Json.Serialization;
using Obstruo.Shared.Contracts;
using Obstruo.Shared.Enums;

namespace Obstruo.Shared.Messages;

public sealed record CommandMessage : IObstrouMessage
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }

    [JsonPropertyName("messageType")]
    public string MessageType => "Command";

    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("commandType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required ServiceCommand CommandType { get; init; }

    /// <summary>
    /// Command-specific arguments. Expected keys per command:
    ///   AddDomain        → { "domain": "example.com", "category": "Custom" }
    ///   RemoveDomain     → { "domain": "example.com" }
    ///   AddWhitelist     → { "domain": "example.com" }
    ///   RemoveWhitelist  → { "domain": "example.com" }
    ///   EmergencyStop    → { "minutes": "15" }  (alias: "durationMinutes")
    ///   EmergencyResume  → (no payload)
    ///   GetStatus        → (no payload)
    ///   GetMetrics       → (no payload)
    ///   GetBlocklist     → (no payload — read-only, no auth required)
    ///   GetSetupState    → (no payload — read-only, no auth required)
    ///   SetCredential    → { "key": "pin_hash" | "password_hash" | "recovery_code_hash",
    ///                        "value": "<PLAINTEXT — service hashes it, never the UI>" }
    ///   VerifyCredential → (no payload — plaintext goes in Credential)
    ///   SyncBlocklist    → (no payload)
    ///   UpdateConfig     → { "key": "...", "value": "..." }
    /// </summary>
    [JsonPropertyName("payload")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Payload { get; init; }

    /// <summary>
    /// The PLAINTEXT PIN or password entered by the user, sent over the
    /// local-machine, ACL-restricted named pipe. The service verifies it
    /// with BCrypt against the stored hash. The UI must NEVER hash this
    /// value itself — bcrypt hashes are salted, so a UI-side hash can
    /// never match the stored hash, and a fixed-salt scheme would turn
    /// the hash itself into the credential (pass-the-hash).
    ///
    /// Required for: AddDomain, RemoveDomain, VerifyCredential,
    ///               SetCredential (only after initial setup is complete).
    /// Null for read-only commands (GetStatus, GetMetrics, GetBlocklist,
    /// GetSetupState) and for SetCredential during first-run bootstrap.
    /// </summary>
    [JsonPropertyName("credential")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Credential { get; init; }
}