using Berkeley.Contracts;
using Berkeley.Engine.Clocks;
using Berkeley.Engine.Runtime;
using Berkeley.Engine.Tests.Fakes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Berkeley.Engine.Tests;

[TestClass]
public class WorkerSyncHandlerTests
{
    private FakeNetworkTransport _transport = null!;
    private FakeClock _clock = null!;
    private FakeLeadershipService _leadership = null!;
    private FakeAuditSink _audit = null!;
    private NodeConfig _config = null!;
    private NodeIdentity _identity = null!;
    private WorkerSyncHandler _handler = null!;

    [TestInitialize]
    public void Setup()
    {
        _transport = new FakeNetworkTransport();
        _clock = new FakeClock(initialUtcMs: 50_000, hasPermission: true);
        _leadership = new FakeLeadershipService { CurrentNodeId = "Worker1" };
        _leadership.SetLeader("Master1", sessionId: 1);
        _audit = new FakeAuditSink();
        _identity = new NodeIdentity("Worker1", Guid.NewGuid());
        _config = new NodeConfig(
            NodeId: "Worker1",
            DisplayName: "Worker 1",
            ListenAddress: "127.0.0.1",
            ListenPort: 8001,
            Peers: [],
            OperationParams: new NodeOperationParams()
        );

        _handler = new WorkerSyncHandler(_transport, _clock, _leadership, _audit, _config, _identity);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _handler.Dispose();
    }

    [TestMethod]
    [Description("Worker nhận TIME_REQUEST hợp lệ -> phản hồi TIME_RESPONSE chứa T2, T3")]
    public void HandleTimeRequest_Valid_SendsTimeResponse()
    {
        _clock.SetUtcMs(55_000);

        var request = MessageEnvelope.Create(
            messageType: MessageTypes.TimeRequest,
            sourceNodeId: "Master1",
            sourceBootId: Guid.NewGuid(),
            targetNodeId: "Worker1",
            payload: new TimeRequestPayload(T1MasterSendUtcMs: 50_000),
            roundId: "round-1",
            leaderSessionId: 1
        );

        _transport.SimulateReceive(request);

        Assert.AreEqual(1, _transport.SentMessages.Count);
        var (target, msg) = _transport.SentMessages[0];
        Assert.AreEqual("Master1", target);
        Assert.AreEqual(MessageTypes.TimeResponse, msg.MessageType);
        Assert.AreEqual("round-1", msg.RoundId);
        Assert.AreEqual(1L, msg.LeaderSessionId);

        var payload = msg.DeserializePayload<TimeResponsePayload>();
        Assert.IsNotNull(payload);
        Assert.AreEqual(50_000L, payload.T1MasterSendUtcMs);
        Assert.AreEqual(55_000L, payload.T2WorkerRecvUtcMs);
        Assert.AreEqual(55_000L, payload.T3WorkerSendUtcMs);
    }

    [TestMethod]
    [Description("Worker từ chối TIME_REQUEST nếu node gửi không phải là Master hiện tại")]
    public void HandleTimeRequest_FromNonMaster_Ignored()
    {
        var request = MessageEnvelope.Create(
            messageType: MessageTypes.TimeRequest,
            sourceNodeId: "FakeMaster",
            sourceBootId: Guid.NewGuid(),
            targetNodeId: "Worker1",
            payload: new TimeRequestPayload(50_000),
            roundId: "round-1",
            leaderSessionId: 1
        );

        _transport.SimulateReceive(request);

        Assert.AreEqual(0, _transport.SentMessages.Count);
    }

    [TestMethod]
    [Description("Worker từ chối TIME_REQUEST từ phiên LeaderSession cũ")]
    public void HandleTimeRequest_StaleLeaderSession_Ignored()
    {
        var request = MessageEnvelope.Create(
            messageType: MessageTypes.TimeRequest,
            sourceNodeId: "Master1",
            sourceBootId: Guid.NewGuid(),
            targetNodeId: "Worker1",
            payload: new TimeRequestPayload(50_000),
            roundId: "round-1",
            leaderSessionId: 0 // Phiên cũ hơn phiên 1
        );

        _transport.SimulateReceive(request);

        Assert.AreEqual(0, _transport.SentMessages.Count);
    }

    [TestMethod]
    [Description("Worker nhận ADJUST hợp lệ -> Áp dụng Step và gửi ADJUST_RESULT (Applied)")]
    public void HandleAdjust_Valid_AppliesStepAndSendsResult()
    {
        _clock.SetUtcMs(100_000);

        var adjustMsg = MessageEnvelope.Create(
            messageType: MessageTypes.Adjust,
            sourceNodeId: "Master1",
            sourceBootId: Guid.NewGuid(),
            targetNodeId: "Worker1",
            payload: new AdjustPayload(AdjustmentMs: 250, SentAtUtcMs: 100_000, MaxAgeMs: 5000),
            roundId: "round-1",
            leaderSessionId: 1
        );

        _transport.SimulateReceive(adjustMsg);

        // Kiểm tra đồng hồ đã được cập nhật
        Assert.AreEqual(100_250L, _clock.ReadUtcMs());

        // Kiểm tra message ADJUST_RESULT gửi về Master
        Assert.AreEqual(1, _transport.SentMessages.Count);
        var (target, msg) = _transport.SentMessages[0];
        Assert.AreEqual("Master1", target);
        Assert.AreEqual(MessageTypes.AdjustResult, msg.MessageType);

        var payload = msg.DeserializePayload<AdjustResultPayload>();
        Assert.IsNotNull(payload);
        Assert.AreEqual(AdjustmentStatus.Applied, payload.Result.Status);
        Assert.AreEqual(250.0, payload.Result.RequestedAdjustmentMs);
        Assert.AreEqual(100_000L, payload.Result.ClockBeforeUtcMs);
        Assert.AreEqual(100_250L, payload.Result.ClockAfterUtcMs);
    }

    [TestMethod]
    [Description("Worker nhận ADJUST trùng MessageId -> Không áp dụng lần 2, báo Rejected")]
    public void HandleAdjust_DuplicateMessageId_Idempotent()
    {
        _clock.SetUtcMs(100_000);
        var msgId = Guid.NewGuid();

        var adjustMsg = MessageEnvelope.Create(
            messageType: MessageTypes.Adjust,
            sourceNodeId: "Master1",
            sourceBootId: Guid.NewGuid(),
            targetNodeId: "Worker1",
            payload: new AdjustPayload(AdjustmentMs: 500, SentAtUtcMs: 100_000, MaxAgeMs: 5000),
            roundId: "round-1",
            leaderSessionId: 1,
            messageId: msgId
        );

        // Lần 1: Thành công
        _transport.SimulateReceive(adjustMsg);
        Assert.AreEqual(100_500L, _clock.ReadUtcMs());
        Assert.AreEqual(1, _transport.SentMessages.Count);

        // Lần 2: Trùng lặp
        _transport.SimulateReceive(adjustMsg);

        // Không được cộng thêm 500ms nữa
        Assert.AreEqual(100_500L, _clock.ReadUtcMs());
        Assert.AreEqual(2, _transport.SentMessages.Count);

        var secondResult = _transport.SentMessages[1].Message.DeserializePayload<AdjustResultPayload>();
        Assert.IsNotNull(secondResult);
        Assert.AreEqual(AdjustmentStatus.Rejected, secondResult.Result.Status);
    }

    [TestMethod]
    [Description("Worker nhận ADJUST đã hết hạn (age > MaxAgeMs) -> Báo Rejected")]
    public void HandleAdjust_Expired_Rejected()
    {
        _clock.SetUtcMs(100_000);

        var adjustMsg = MessageEnvelope.Create(
            messageType: MessageTypes.Adjust,
            sourceNodeId: "Master1",
            sourceBootId: Guid.NewGuid(),
            targetNodeId: "Worker1",
            // SentAt cách đây 10,000ms nhưng MaxAge chỉ 3,000ms
            payload: new AdjustPayload(AdjustmentMs: 500, SentAtUtcMs: 90_000, MaxAgeMs: 3000),
            roundId: "round-1",
            leaderSessionId: 1
        );

        _transport.SimulateReceive(adjustMsg);

        // Đồng hồ không đổi
        Assert.AreEqual(100_000L, _clock.ReadUtcMs());

        Assert.AreEqual(1, _transport.SentMessages.Count);
        var payload = _transport.SentMessages[0].Message.DeserializePayload<AdjustResultPayload>();
        Assert.IsNotNull(payload);
        Assert.AreEqual(AdjustmentStatus.Rejected, payload.Result.Status);
    }
}

