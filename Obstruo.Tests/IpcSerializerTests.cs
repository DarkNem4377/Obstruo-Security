using Obstruo.Shared.Contracts;
using Obstruo.Shared.Enums;
using Obstruo.Shared.Messages;

namespace Obstruo.Tests;

/// <summary>
/// The IPC wire format is NDJSON with a messageType discriminator. These tests
/// pin the roundtrip for every message type the UI dispatches, plus the
/// malformed-input behavior the pipe read loops rely on (return false, never
/// throw).
/// </summary>
public class IpcSerializerTests
{
    [Fact]
    public void Serialize_ProducesSingleLine()
    {
        var json = IpcSerializer.Serialize(new HeartbeatMessage
        {
            Timestamp = DateTime.UtcNow.ToString("O"),
            ServiceOk = true,
            ProtectionState = ProtectionState.Active,
            BlockCountTotal = 42
        });

        Assert.DoesNotContain('\n', json);
        Assert.DoesNotContain('\r', json);
    }

    [Fact]
    public void Heartbeat_Roundtrips()
    {
        var json = IpcSerializer.Serialize(new HeartbeatMessage
        {
            Timestamp = "2026-07-12T00:00:00.0000000Z",
            ServiceOk = true,
            ProtectionState = ProtectionState.Active,
            BlockCountTotal = 42
        });

        Assert.True(IpcSerializer.TryDeserialize(json, out var message));
        var hb = Assert.IsType<HeartbeatMessage>(message);
        Assert.Equal(42, hb.BlockCountTotal);
        Assert.Equal(ProtectionState.Active, hb.ProtectionState);
    }

    [Fact]
    public void LogEvent_Roundtrips()
    {
        var json = IpcSerializer.Serialize(new LogEventMessage
        {
            Timestamp = "2026-07-12T00:00:00.0000000Z",
            Domain = "blocked.example",
            Category = BlockCategory.Malware,
            Severity = Severity.Critical,
            DeviceName = "192.168.1.20",
            Mitre = "T1071.004"
        });

        Assert.True(IpcSerializer.TryDeserialize(json, out var message));
        var le = Assert.IsType<LogEventMessage>(message);
        Assert.Equal("blocked.example", le.Domain);
        Assert.Equal(BlockCategory.Malware, le.Category);
        Assert.Equal("192.168.1.20", le.DeviceName);
    }

    [Fact]
    public void MetricsUpdate_Roundtrips()
    {
        var json = IpcSerializer.Serialize(new MetricsUpdateMessage
        {
            Timestamp = "2026-07-12T00:00:00.0000000Z",
            BlocksToday = 10,
            BlocksWeek = 70,
            ByCategory = [new CategoryCount { Category = "Adult", Count = 10 }],
            TopDomains = [new DomainHit { Domain = "x.example", Hits = 3, Category = "Adult" }],
            HourlyBars = [new HourlyBar { Hour = 13, Count = 4 }]
        });

        Assert.True(IpcSerializer.TryDeserialize(json, out var message));
        var metrics = Assert.IsType<MetricsUpdateMessage>(message);
        Assert.Equal(10, metrics.BlocksToday);
        Assert.Equal(70, metrics.BlocksWeek);
        Assert.Single(metrics.TopDomains);
        Assert.Equal(13, metrics.HourlyBars[0].Hour);
    }

    [Fact]
    public void StatusUpdate_Roundtrips()
    {
        var json = IpcSerializer.Serialize(new StatusUpdateMessage
        {
            Timestamp = "2026-07-12T00:00:00.0000000Z",
            ProtectionState = ProtectionState.DisabledTemporary,
            UptimeSeconds = 1234,
            BlockCount = 7,
            ThreatLevel = ThreatLevel.Low
        });

        Assert.True(IpcSerializer.TryDeserialize(json, out var message));
        var status = Assert.IsType<StatusUpdateMessage>(message);
        Assert.Equal(ProtectionState.DisabledTemporary, status.ProtectionState);
    }

    [Fact]
    public void StatusUpdate_FromPre103Service_DefaultsHealthyWithNoCounts()
    {
        // A 1.0.2 service sends no upstreamHealthy/ruleCounts — the UI must
        // treat that as healthy and keep its loading placeholders.
        var json = """{"schemaVersion":1,"timestamp":"2026-07-12T00:00:00.0000000Z","messageType":"StatusUpdate","protectionState":"Active","uptimeSeconds":5,"blockCount":0,"threatLevel":"Low"}""";

        Assert.True(IpcSerializer.TryDeserialize(json, out var message));
        var status = Assert.IsType<StatusUpdateMessage>(message);
        Assert.True(status.UpstreamHealthy);
        Assert.Null(status.RuleCounts);
    }

    [Fact]
    public void StatusUpdate_RoundtripsUpstreamHealthAndRuleCounts()
    {
        var json = IpcSerializer.Serialize(new StatusUpdateMessage
        {
            Timestamp = "2026-07-12T00:00:00.0000000Z",
            ProtectionState = ProtectionState.Active,
            UptimeSeconds = 1,
            BlockCount = 0,
            ThreatLevel = ThreatLevel.Low,
            UpstreamHealthy = false,
            RuleCounts = new Dictionary<string, int> { ["Adult"] = 5842, ["Custom"] = 3 }
        });

        Assert.True(IpcSerializer.TryDeserialize(json, out var message));
        var status = Assert.IsType<StatusUpdateMessage>(message);
        Assert.False(status.UpstreamHealthy);
        Assert.Equal(5842, status.RuleCounts!["Adult"]);
        Assert.Equal(3, status.RuleCounts["Custom"]);
    }

    [Fact]
    public void Command_Roundtrips_WithPayloadAndCredential()
    {
        var command = new CommandMessage
        {
            Timestamp = "2026-07-12T00:00:00.0000000Z",
            RequestId = "req-1",
            CommandType = ServiceCommand.AddWhitelist,
            Payload = new Dictionary<string, string> { ["domain"] = "ok.example" },
            Credential = "123456"
        };

        var json = IpcSerializer.Serialize(command);
        Assert.True(IpcSerializer.TryDeserializeCommand(json, out var parsed));
        Assert.Equal(ServiceCommand.AddWhitelist, parsed!.CommandType);
        Assert.Equal("ok.example", parsed.Payload!["domain"]);
        Assert.Equal("123456", parsed.Credential);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]                                     // no messageType
    [InlineData("{\"messageType\":\"Bogus\"}")]            // unknown discriminator
    [InlineData("")]
    public void TryDeserialize_MalformedInput_ReturnsFalse_NeverThrows(string input)
    {
        Assert.False(IpcSerializer.TryDeserialize(input, out var message));
        Assert.Null(message);
    }

    [Fact]
    public void TryDeserializeCommand_MalformedInput_ReturnsFalse()
        => Assert.False(IpcSerializer.TryDeserializeCommand("garbage{", out _));
}
