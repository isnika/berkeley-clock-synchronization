using System.IO;
using Berkeley.Contracts;
using Berkeley.Desktop.Persistence;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Berkeley.Desktop.Tests;

[TestClass]
public class SqliteAuditSinkTests
{
    private string _dbPath = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_audit_{Guid.NewGuid():N}.db");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    [TestMethod]
    public async Task SqliteAuditSink_CanRecordAndRetrieveEvents()
    {
        await using var sink = new SqliteAuditSink(_dbPath);

        sink.Record(new AuditEvent(
            Sequence: 1,
            OccurredAt: DateTimeOffset.UtcNow,
            MonotonicMs: 100,
            SourceNodeId: "Node1",
            Kind: AuditEventKind.MessageSent,
            Summary: "Gửi TIME_REQUEST"
        ));

        // Đợi background channel flush
        await Task.Delay(200);

        var events = await sink.GetRecentEventsAsync(10);
        Assert.IsTrue(events.Count >= 1);
        Assert.AreEqual("Node1", events[0].SourceNodeId);
        Assert.AreEqual("Gửi TIME_REQUEST", events[0].Summary);
    }

    [TestMethod]
    public async Task SqliteAuditSink_CanRecordRoundSummary()
    {
        await using var sink = new SqliteAuditSink(_dbPath);

        var summary = new RoundSummary(
            RoundId: "R-001",
            MasterNodeId: "Node1",
            LeaderSessionId: 1,
            RoundStartedAt: DateTimeOffset.UtcNow.AddSeconds(-2),
            RoundEndedAt: DateTimeOffset.UtcNow,
            AcceptedSamples: [],
            RejectedSamples: [],
            AverageOffsetMs: 150.5,
            AdjustmentResults: [],
            RemeasuredSpreadMs: 5.2,
            Outcome: RoundOutcome.Success
        );

        sink.RecordRoundSummary(summary);
        await Task.Delay(300);

        var rounds = await sink.GetRecentRoundsAsync(10);
        Assert.AreEqual(1, rounds.Count);
        Assert.AreEqual("R-001", rounds[0].RoundId);
        Assert.AreEqual(150.5, rounds[0].AverageOffsetMs);
        Assert.AreEqual("Success", rounds[0].Outcome);
    }

    [TestMethod]
    public async Task SqliteAuditSink_CanExportToCsv()
    {
        await using var sink = new SqliteAuditSink(_dbPath);
        sink.Record(new AuditEvent(1, DateTimeOffset.UtcNow, 50, "Node2", AuditEventKind.ClockAdjusted, "Step +100ms"));
        await Task.Delay(200);

        string csvPath = Path.Combine(Path.GetTempPath(), $"export_{Guid.NewGuid():N}.csv");
        await sink.ExportAuditToCsvAsync(csvPath);

        Assert.IsTrue(File.Exists(csvPath));
        string content = await File.ReadAllTextAsync(csvPath);
        Assert.IsTrue(content.Contains("Step +100ms"));
        File.Delete(csvPath);
    }
}
