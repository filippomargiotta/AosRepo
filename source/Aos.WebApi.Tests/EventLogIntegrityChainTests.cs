using System.Text.Json;
using Aos.WebApi.Models;
using Aos.WebApi.Services;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class EventLogIntegrityChainTests
{
    private const string TestHmacKey = "test-hmac-key";
    private const string TestHmacKeyId = "test-key";

    [Fact]
    public void SignEntries_ProducesCurrentSchemaAndValidChain()
    {
        var chain = CreateChain();
        var records = chain.SignEntries(CreateEntries());

        Assert.All(records, record => Assert.Equal(SchemaVersions.CurrentEventLogSchemaVersion, record.SchemaVersion));
        Assert.True(chain.TryValidateRecords(records, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void TryValidateRecords_WhenPayloadIsModified_ReturnsFirstBrokenLine()
    {
        var chain = CreateChain();
        var records = chain.SignEntries(CreateEntries()).ToArray();
        records[0] = records[0] with
        {
            Entry = records[0].Entry with
            {
                Data = new { message = "tampered", manifestVersion = SchemaVersions.CurrentManifestVersion }
            }
        };

        Assert.False(chain.TryValidateRecords(records, out var error));
        Assert.Equal("Event log line 1 chainMac is invalid.", error);
    }

    [Fact]
    public void TryValidateRecords_WhenSecondRecordIsRemoved_ReturnsFirstBrokenLine()
    {
        var chain = CreateChain();
        var records = chain.SignEntries(CreateEntries()).ToArray();

        Assert.False(chain.TryValidateRecords([records[0], records[2]], out var error));
        Assert.Equal("Event log line 2 previousChainMac does not match the prior chain state.", error);
    }

    [Fact]
    public void TryValidateRecords_WhenRecordsAreReordered_ReturnsFirstBrokenLine()
    {
        var chain = CreateChain();
        var records = chain.SignEntries(CreateEntries()).ToArray();

        Assert.False(chain.TryValidateRecords([records[1], records[0], records[2]], out var error));
        Assert.Equal("Event log line 1 previousChainMac does not match the prior chain state.", error);
    }

    [Fact]
    public void TryValidateRecords_WhenWrongKeyIsUsed_ReturnsFirstBrokenLine()
    {
        var records = CreateChain().SignEntries(CreateEntries());
        var validator = new HmacEventLogIntegrityChain("wrong-key", TestHmacKeyId);

        Assert.False(validator.TryValidateRecords(records, out var error));
        Assert.Equal("Event log line 1 chainMac is invalid.", error);
    }

    private static HmacEventLogIntegrityChain CreateChain() => new(TestHmacKey, TestHmacKeyId);

    private static IReadOnlyList<EventLogEntry> CreateEntries()
    {
        return
        [
            CreateEntry("workflow.hello", "hello-1", new DateTimeOffset(2026, 3, 24, 8, 0, 0, TimeSpan.Zero)),
            CreateEntry("workflow.hello", "hello-2", new DateTimeOffset(2026, 3, 24, 8, 0, 1, TimeSpan.Zero)),
            CreateEntry("workflow.hello", "hello-3", new DateTimeOffset(2026, 3, 24, 8, 0, 2, TimeSpan.Zero))
        ];
    }

    private static EventLogEntry CreateEntry(string eventType, string message, DateTimeOffset occurredAtUtc)
    {
        return new EventLogEntry(
            RunId: "run-1",
            EventType: eventType,
            Data: new { message, manifestVersion = SchemaVersions.CurrentManifestVersion },
            OccurredAtUtc: occurredAtUtc);
    }
}
