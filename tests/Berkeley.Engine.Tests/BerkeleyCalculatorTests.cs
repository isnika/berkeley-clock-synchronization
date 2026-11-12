using System.Text.Json;
using Berkeley.Contracts;
using Berkeley.Engine.Clocks;
using Berkeley.Engine.Synchronization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Berkeley.Engine.Tests;

/// <summary>
/// [A2] Unit test công thức tính offset và RTT từ 4 mốc t1-t4.
/// Tất cả test đều dùng FakeClock — không chạm giờ hệ điều hành.
/// </summary>
[TestClass]
public class OffsetCalculationTests
{
    // ── Test tính offset ───────────────────────────────────────────────────

    [TestMethod]
    [Description("Worker nhanh hơn Master 100ms → offset dương")]
    public void ComputeOffset_WorkerFasterThanMaster_ReturnsPositiveOffset()
    {
        // t1=1000 (Master gửi), t2=1150 (Worker nhận, Worker nhanh hơn 100ms)
        // t3=1160 (Worker gửi lại), t4=1210 (Master nhận)
        // RTT thực = (1210-1000)-(1160-1150) = 210 - 10 = 200ms
        // Offset = ((1150-1000)+(1160-1210))/2 = (150 + (-50))/2 = 50ms ≠ 100ms
        // → offset 50ms vì RTT không đối xứng

        var (offset, rtt) = BerkeleyCalculator.ComputeOffset(
            t1MasterSendUtcMs: 1000,
            t2WorkerRecvUtcMs: 1150,
            t3WorkerSendUtcMs: 1160,
            t4MasterRecvUtcMs: 1210);

        Assert.AreEqual(50.0, offset, delta: 0.001);
        Assert.AreEqual(200.0, rtt, delta: 0.001);
    }

    [TestMethod]
    [Description("Worker chậm hơn Master → offset âm")]
    public void ComputeOffset_WorkerSlowerThanMaster_ReturnsNegativeOffset()
    {
        // Master tại 1000, Worker thực sự tại 900 (chậm hơn 100ms)
        // t2 = 950 (Worker nhận), t3 = 960, t4 = 1210
        var (offset, rtt) = BerkeleyCalculator.ComputeOffset(
            t1MasterSendUtcMs: 1000,
            t2WorkerRecvUtcMs: 950,
            t3WorkerSendUtcMs: 960,
            t4MasterRecvUtcMs: 1210);

        // offset = ((950-1000)+(960-1210))/2 = (-50 + (-250))/2 = -150ms
        Assert.AreEqual(-150.0, offset, delta: 0.001);
        Assert.IsTrue(offset < 0, "Worker chậm hơn → offset phải âm");
    }

    [TestMethod]
    [Description("Hai đồng hồ hoàn toàn đồng bộ → offset = 0")]
    public void ComputeOffset_PerfectSync_ReturnsZeroOffset()
    {
        // RTT = 100ms, trễ 2 chiều đều nhau (50ms mỗi chiều)
        var (offset, rtt) = BerkeleyCalculator.ComputeOffset(
            t1MasterSendUtcMs: 1000,
            t2WorkerRecvUtcMs: 1050,
            t3WorkerSendUtcMs: 1050,
            t4MasterRecvUtcMs: 1100);

        Assert.AreEqual(0.0, offset, delta: 0.001);
        Assert.AreEqual(100.0, rtt, delta: 0.001);
    }
}

/// <summary>
/// [A3] Unit test tính trung bình Berkeley và mức hiệu chỉnh.
/// </summary>
[TestClass]
public class AveragingTests
{
    private static ClockSample MakeSample(string workerId, double offsetMs) =>
        new("round-1", workerId, 0, 0, 0, 0,
            offsetMs, 10, SampleStatus.Valid);

    [TestMethod]
    [Description("Ví dụ 6 máy từ tài liệu: giờ chung = 10:05 (d_avg = -5 phút)")]
    public void ComputeAdjustments_SixNodes_AverageCorrect()
    {
        // Master tại 10:10 (offset=0), Workers lần lượt: -10, -8, -6, -4, -2 phút
        var samples = new List<ClockSample>
        {
            MakeSample("W1", -10 * 60_000),
            MakeSample("W2", -8  * 60_000),
            MakeSample("W3", -6  * 60_000),
            MakeSample("W4", -4  * 60_000),
            MakeSample("W5", -2  * 60_000),
        };

        var result = BerkeleyCalculator.ComputeAdjustments("Master", samples)!;

        // d_avg = (0 + -10 + -8 + -6 + -4 + -2) / 6 = -30/6 = -5 phút
        double expectedAvgMs = -5 * 60_000;
        Assert.AreEqual(expectedAvgMs, result.AverageOffsetMs, delta: 0.001);

        // Master điều chỉnh: a_M = d_avg = -5 phút (nhảy lùi)
        Assert.AreEqual(expectedAvgMs, result.MasterAdjustment.AdjustmentMs, delta: 0.001);

        // W1: a = -5 - (-10) = +5 phút (chỉnh tiến)
        var w1 = result.WorkerAdjustments.First(a => a.NodeId == "W1");
        Assert.AreEqual(5 * 60_000.0, w1.AdjustmentMs, delta: 0.001);

        // W5: a = -5 - (-2) = -3 phút (chỉnh lùi)
        var w5 = result.WorkerAdjustments.First(a => a.NodeId == "W5");
        Assert.AreEqual(-3 * 60_000.0, w5.AdjustmentMs, delta: 0.001);
    }

    [TestMethod]
    [Description("Không có mẫu hợp lệ → trả null")]
    public void ComputeAdjustments_NoValidSamples_ReturnsNull()
    {
        var result = BerkeleyCalculator.ComputeAdjustments("Master", []);
        Assert.IsNull(result);
    }

    [TestMethod]
    [Description("Master cũng phải tự chỉnh giờ của mình")]
    public void ComputeAdjustments_MasterAlsoAdjusts()
    {
        var samples = new List<ClockSample> { MakeSample("W1", 1000) };
        var result = BerkeleyCalculator.ComputeAdjustments("Master", samples)!;

        // d_avg = (0 + 1000) / 2 = 500ms
        // Master tự chỉnh +500ms
        Assert.AreEqual(500.0, result.MasterAdjustment.AdjustmentMs, delta: 0.001);
    }
}

/// <summary>
/// [A4, A5] Unit test FakeClock và xử lý quyền hệ thống.
/// </summary>
[TestClass]
public class ClockTests
{
    [TestMethod]
    [Description("ApplyStep với FakeClock cộng đúng adjustment vào giờ")]
    public void FakeClock_ApplyStep_UpdatesTime()
    {
        var clock = new FakeClock(initialUtcMs: 10_000);
        var result = clock.ApplyStep("N1", "round-1", 500);

        Assert.AreEqual(AdjustmentStatus.Applied, result.Status);
        Assert.AreEqual(10_000L, result.ClockBeforeUtcMs);
        Assert.AreEqual(10_500L, result.ClockAfterUtcMs);
        Assert.AreEqual(10_500L, clock.ReadUtcMs());
    }

    [TestMethod]
    [Description("Thiếu quyền → không áp dụng, trả PermissionDenied")]
    public void FakeClock_NoPermission_ReturnsPermissionDenied()
    {
        var clock = new FakeClock(10_000, hasPermission: false);
        var result = clock.ApplyStep("N1", "round-1", 500);

        Assert.AreEqual(AdjustmentStatus.PermissionDenied, result.Status);
        Assert.AreEqual(10_000L, clock.ReadUtcMs(), "Giờ không được thay đổi");
    }

    [TestMethod]
    [Description("ApplyStep âm (chỉnh lùi) → giờ giảm")]
    public void FakeClock_NegativeAdjustment_ClocksGoBack()
    {
        var clock = new FakeClock(initialUtcMs: 10_000);
        clock.ApplyStep("N1", "round-1", -3000);

        Assert.AreEqual(7_000L, clock.ReadUtcMs(), "Giờ phải nhảy lùi về 7000ms");
    }

    [TestMethod]
    [Description("API Windows thất bại → trả Failed, giờ không thay đổi")]
    public void FakeClock_SimulatedApiFail_ReturnsFailed()
    {
        var clock = new FakeClock(10_000);
        clock.SimulateNextStepFail("Simulated kernel error");

        var result = clock.ApplyStep("N1", "round-1", 500);

        Assert.AreEqual(AdjustmentStatus.Failed, result.Status);
        Assert.AreEqual(10_000L, clock.ReadUtcMs());
    }
}

/// <summary>
/// [A1] Kiểm tra serialize/deserialize MessageEnvelope và Payload qua JSON.
/// </summary>
[TestClass]
public class MessageEnvelopeTests
{
    [TestMethod]
    [Description("MessageEnvelope giữ đúng payload qua JSON roundtrip")]
    public void MessageEnvelope_JsonRoundtrip_PreservesTimeResponsePayload()
    {
        var bootId = Guid.NewGuid();
        var original = MessageEnvelope.Create(
            messageType: MessageTypes.TimeResponse,
            sourceNodeId: "W1",
            sourceBootId: bootId,
            targetNodeId: "Master",
            payload: new TimeResponsePayload(1000, 1050, 1060),
            roundId: "round-1",
            leaderSessionId: 3
        );

        string json = JsonSerializer.Serialize(original);
        var decoded = JsonSerializer.Deserialize<MessageEnvelope>(json)!;
        var payload = decoded.DeserializePayload<TimeResponsePayload>()!;

        Assert.AreEqual(MessageTypes.TimeResponse, decoded.MessageType);
        Assert.AreEqual("W1", decoded.SourceNodeId);
        Assert.AreEqual(bootId, decoded.SourceBootId);
        Assert.AreEqual("round-1", decoded.RoundId);
        Assert.AreEqual(3L, decoded.LeaderSessionId);
        Assert.AreEqual(1000L, payload.T1MasterSendUtcMs);
        Assert.AreEqual(1050L, payload.T2WorkerRecvUtcMs);
        Assert.AreEqual(1060L, payload.T3WorkerSendUtcMs);
    }
}


