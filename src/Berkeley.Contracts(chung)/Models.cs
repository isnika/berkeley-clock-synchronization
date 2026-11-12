using System.Text.Json;

namespace Berkeley.Contracts;

/// <summary>
/// Định danh một Node trong cụm.
/// NodeId: cố định, do cấu hình. BootId: đổi mỗi lần khởi động lại.
/// </summary>
public record NodeIdentity(string NodeId, Guid BootId);

/// <summary>
/// Cấu hình đầy đủ của một Node khi khởi động.
/// </summary>
public record NodeConfig(
    string NodeId,
    string DisplayName,
    string ListenAddress,
    int ListenPort,
    List<PeerConfig> Peers,
    NodeOperationParams OperationParams
);

public record PeerConfig(string NodeId, string Address, int Port);

/// <summary>
/// Các tham số vận hành — ngưỡng timeout, heartbeat, v.v.
/// Phải được chốt trong đặc tả trước khi triển khai phần phụ thuộc.
/// </summary>
public record NodeOperationParams(
    int HeartbeatIntervalMs = 2000,
    int HeartbeatTimeoutMs = 6000,
    int ElectionTimeoutMs = 5000,
    int SampleTimeoutMs = 3000,
    int SampleMaxAgeMs = 10000,
    int RoundTimeoutMs = 30000
);

/// <summary>
/// Phiên lãnh đạo — xác định Master hiện hành và số thứ tự phiên.
/// Mỗi lần đổi Master thì LeaderSessionId tăng lên.
/// </summary>
public record LeaderSession(string MasterNodeId, long LeaderSessionId);

/// <summary>
/// Hằng số loại thông điệp giao thức TCP thống nhất cho cả nhóm.
/// </summary>
public static class MessageTypes
{
    public const string Hello = "HELLO";
    public const string Ping = "PING";
    public const string Pong = "PONG";
    public const string Election = "ELECTION";
    public const string Ok = "OK";
    public const string Coordinator = "COORDINATOR";
    public const string TimeRequest = "TIME_REQUEST";
    public const string TimeResponse = "TIME_RESPONSE";
    public const string Adjust = "ADJUST";
    public const string AdjustResult = "ADJUST_RESULT";
    public const string RoundResult = "ROUND_RESULT";
    public const string ClusterStatus = "CLUSTER_STATUS";
}

/// <summary>
/// Phong bì thông điệp giao thức dùng chung cho mọi message TCP.
/// Payload lưu dạng JsonElement? để serialize/deserialize an toàn qua System.Text.Json.
/// </summary>
public record MessageEnvelope(
    int ProtocolVersion,
    string MessageType,
    string SourceNodeId,
    Guid SourceBootId,
    string TargetNodeId,
    Guid MessageId,
    string? RoundId,
    long? LeaderSessionId,
    JsonElement? Payload
)
{
    /// <summary>Tạo MessageEnvelope kèm payload được serialize sang JsonElement.</summary>
    public static MessageEnvelope Create<TPayload>(
        string messageType,
        string sourceNodeId,
        Guid sourceBootId,
        string targetNodeId,
        TPayload payload,
        string? roundId = null,
        long? leaderSessionId = null,
        int protocolVersion = 1,
        Guid? messageId = null) =>
        new(
            ProtocolVersion: protocolVersion,
            MessageType: messageType,
            SourceNodeId: sourceNodeId,
            SourceBootId: sourceBootId,
            TargetNodeId: targetNodeId,
            MessageId: messageId ?? Guid.NewGuid(),
            RoundId: roundId,
            LeaderSessionId: leaderSessionId,
            Payload: JsonSerializer.SerializeToElement(payload)
        );

    /// <summary>Giải mã Payload về kiểu DTO mong muốn.</summary>
    public TPayload? DeserializePayload<TPayload>() =>
        Payload is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } element
            ? element.Deserialize<TPayload>()
            : default;
}

// ── Payload DTOs cho các loại thông điệp ───────────────────────────────────

public record HelloPayload(string NodeId, Guid BootId, int ProtocolVersion, string DisplayName);
public record HeartbeatPayload(long SentUtcMs, long MonotonicMs);
public record ElectionPayload(string CandidateNodeId, long? LeaderSessionId = null);
public record TimeRequestPayload(long T1MasterSendUtcMs);
public record TimeResponsePayload(long T1MasterSendUtcMs, long T2WorkerRecvUtcMs, long T3WorkerSendUtcMs);
public record AdjustPayload(double AdjustmentMs, long SentAtUtcMs, int MaxAgeMs);
public record AdjustResultPayload(AdjustmentResult Result);
public record RoundResultPayload(RoundSummary Summary);
public record ClusterStatusPayload(NodeSnapshot Snapshot);

/// <summary>
/// Kết quả một mẫu đo Berkeley (4 mốc t1-t4).
/// </summary>
public record ClockSample(
    string RoundId,
    string WorkerNodeId,
    /// <summary>Giờ Master khi gửi TIME_REQUEST (UTC ms)</summary>
    long T1MasterSendUtcMs,
    /// <summary>Giờ Worker khi nhận TIME_REQUEST (UTC ms)</summary>
    long T2WorkerRecvUtcMs,
    /// <summary>Giờ Worker khi gửi TIME_RESPONSE (UTC ms)</summary>
    long T3WorkerSendUtcMs,
    /// <summary>Giờ Master khi nhận TIME_RESPONSE (UTC ms)</summary>
    long T4MasterRecvUtcMs,
    /// <summary>Offset ước lượng: d = ((t2-t1)+(t3-t4))/2. Dương = Worker nhanh hơn Master.</summary>
    double OffsetMs,
    /// <summary>RTT ước lượng: (t4-t1)-(t3-t2)</summary>
    double RttMs,
    SampleStatus Status,
    string? RejectionReason = null
);

public enum SampleStatus
{
    Valid,
    RejectedTimeout,
    RejectedStaleRound,
    RejectedBadFormat,
    RejectedClockJump,
    RejectedHighRtt
}

/// <summary>
/// Kết quả áp dụng Step trên một Node.
/// </summary>
public record AdjustmentResult(
    string NodeId,
    string RoundId,
    double RequestedAdjustmentMs,
    long ClockBeforeUtcMs,
    long ClockAfterUtcMs,
    AdjustmentStatus Status,
    string? ErrorMessage = null
);

public enum AdjustmentStatus
{
    Applied,
    Rejected,       // lệnh trùng hoặc hết hiệu lực
    Failed,         // lỗi API Windows
    PermissionDenied
}

/// <summary>
/// Tổng kết một vòng đồng bộ hoàn chỉnh.
/// </summary>
public record RoundSummary(
    string RoundId,
    string MasterNodeId,
    long LeaderSessionId,
    DateTimeOffset RoundStartedAt,
    DateTimeOffset RoundEndedAt,
    List<ClockSample> AcceptedSamples,
    List<ClockSample> RejectedSamples,
    double AverageOffsetMs,
    List<AdjustmentResult> AdjustmentResults,
    double? RemeasuredSpreadMs,
    RoundOutcome Outcome
);

public enum RoundOutcome
{
    Success,
    PartialSuccess,   // một số máy không áp dụng được
    Cancelled,        // Master thay đổi hoặc không đủ mẫu
    Failed
}

/// <summary>
/// Snapshot trạng thái Node tại một thời điểm — GUI dùng để hiển thị.
/// </summary>
public record NodeSnapshot(
    string NodeId,
    string DisplayName,
    NodeRole Role,
    string? CurrentMasterId,
    long? LeaderSessionId,
    long LocalClockUtcMs,
    bool HasSetTimePermission,
    string? ActiveRoundId,
    RoundPhase RoundPhase,
    List<PeerStatus> KnownPeers,
    DateTimeOffset SnapshotTakenAt
);

public enum NodeRole { Master, Worker, Electing, Standalone }
public enum RoundPhase { Idle, Measuring, Adjusting, Verifying, Done }

public record PeerStatus(
    string NodeId,
    string DisplayName,
    PeerConnectionState ConnectionState,
    double? LastRttMs,
    double? LastOffsetMs,
    double? LastAdjustmentMs,
    DateTimeOffset? LastSeenAt
);

public enum PeerConnectionState { Connected, Disconnected, Unknown }

/// <summary>
/// Sự kiện audit ghi lại mọi hoạt động quan trọng.
/// </summary>
public record AuditEvent(
    long Sequence,
    DateTimeOffset OccurredAt,
    long MonotonicMs,
    string SourceNodeId,
    AuditEventKind Kind,
    string Summary,
    object? Detail = null
);

public enum AuditEventKind
{
    MessageSent,
    MessageReceived,
    MessageRejected,
    SendError,
    ClockRead,
    ClockAdjusted,
    ClockAdjustFailed,
    RoundStarted,
    RoundEnded,
    LeaderChanged,
    PeerConnected,
    PeerDisconnected,
    PermissionError
}

