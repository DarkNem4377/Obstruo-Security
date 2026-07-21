using System.Text.Json.Serialization;
using Obstruo.Shared.Contracts;

namespace Obstruo.Shared.Messages;

public sealed record MetricsUpdateMessage : IObstrouMessage
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }

    [JsonPropertyName("messageType")]
    public string MessageType => "MetricsUpdate";

    [JsonPropertyName("blocksToday")]
    public required int BlocksToday { get; init; }

    [JsonPropertyName("blocksWeek")]
    public required int BlocksWeek { get; init; }

    [JsonPropertyName("byCategory")]
    public required IReadOnlyList<CategoryCount> ByCategory { get; init; }

    [JsonPropertyName("topDomains")]
    public required IReadOnlyList<DomainHit> TopDomains { get; init; }

    [JsonPropertyName("hourlyBars")]
    public required IReadOnlyList<HourlyBar> HourlyBars { get; init; }

    // Block-decision latency (finding M1). Optional/defaulted so older clients and
    // existing construction sites are unaffected; 0 means "no samples yet".
    [JsonPropertyName("blockLatencyP50Ms")]
    public double BlockLatencyP50Ms { get; init; }

    [JsonPropertyName("blockLatencyP95Ms")]
    public double BlockLatencyP95Ms { get; init; }

    [JsonPropertyName("blockLatencySamples")]
    public int BlockLatencySamples { get; init; }
}

public sealed record CategoryCount
{
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }
}

public sealed record DomainHit
{
    [JsonPropertyName("domain")]
    public required string Domain { get; init; }

    [JsonPropertyName("hits")]
    public required int Hits { get; init; }

    [JsonPropertyName("category")]
    public required string Category { get; init; }
}

public sealed record HourlyBar
{
    [JsonPropertyName("hour")]
    public required int Hour { get; init; }   // 0–23

    [JsonPropertyName("count")]
    public required int Count { get; init; }
}