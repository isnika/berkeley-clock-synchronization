namespace Berkeley.Contracts;

// ─────────────────────────────────────────────
// Interface TV1 cung cấp
// ─────────────────────────────────────────────

/// <summary>
/// [TV1 triển khai] Đọc và áp dụng Step lên đồng hồ hệ thống.
/// Dùng implementation thật (WindowsClock) trên runtime; dùng FakeClock trong test.
/// </summary>
public interface ISystemClock
{
    /// <summary>Đọc giờ hiện tại theo UTC (ms từ epoch).</summary>
    long ReadUtcMs();

    /// <summary>Đọc giá trị monotonic (ms từ khi khởi động). Chỉ dùng đo khoảng thời gian.</summary>
    long ReadMonotonicMs();

    /// <summary>
    /// Áp dụng Step: cộng adjustmentMs vào giờ hệ thống.
    /// Trả về kết quả áp dụng. KHÔNG ném exception — lỗi được trả qua AdjustmentResult.
    /// </summary>
    AdjustmentResult ApplyStep(string nodeId, string roundId, double adjustmentMs);

    /// <summary>Kiểm tra ứng dụng có quyền chỉnh giờ hệ điều hành không.</summary>
    bool HasSetTimePermission();
}

/// <summary>
/// [TV1 triển khai] Chỉ đọc timestamp và monotonic — dành cho TV2 ghi mốc tại tầng I/O.
/// Không có quyền gọi ApplyStep.
/// </summary>
public interface IClockReader
{
    long ReadUtcMs();
    long ReadMonotonicMs();
}

/// <summary>
/// [TV1 triển khai] API điều khiển Node — TV3 (GUI) dùng để gửi lệnh và nhận snapshot.
/// </summary>
public interface INodeApplication
{
    /// <summary>Khởi động Node: bắt đầu lắng nghe, kết nối peers, tham gia cluster.</summary>
    Task StartAsync(NodeConfig config, CancellationToken ct = default);

    /// <summary>Dừng Node gracefully.</summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// Kích hoạt một vòng đồng bộ ngay lập tức (nếu Node là Master).
    /// Trả về false nếu Node không phải Master hoặc đang có vòng khác chạy.
    /// </summary>
    Task<bool> TriggerSyncNowAsync(CancellationToken ct = default);

    /// <summary>Lấy snapshot trạng thái hiện tại của Node.</summary>
    NodeSnapshot GetSnapshot();

    /// <summary>Sự kiện phát mỗi khi snapshot thay đổi — GUI subscribe để cập nhật UI.</summary>
    event Action<NodeSnapshot> SnapshotChanged;

    /// <summary>Sự kiện phát mỗi khi một vòng kết thúc.</summary>
    event Action<RoundSummary> RoundCompleted;
}

// ─────────────────────────────────────────────
// Interface TV2 cung cấp (TV1 sử dụng)
// ─────────────────────────────────────────────

/// <summary>
/// [TV2 triển khai] Vận chuyển message TCP tới/từ các peers.
/// </summary>
public interface INetworkTransport
{
    /// <summary>Gửi message tới một Node cụ thể. Trả về false nếu gửi thất bại.</summary>
    Task<bool> SendAsync(string targetNodeId, MessageEnvelope message, CancellationToken ct = default);

    /// <summary>Gửi broadcast tới tất cả Node đang kết nối.</summary>
    Task BroadcastAsync(MessageEnvelope message, CancellationToken ct = default);

    /// <summary>Sự kiện khi nhận được message từ peer.</summary>
    event Action<MessageEnvelope> MessageReceived;

    /// <summary>Lấy danh sách Node đang kết nối.</summary>
    IReadOnlyList<string> GetConnectedNodeIds();
}

/// <summary>
/// [TV2 triển khai] Trạng thái Master hiện hành và sự kiện thay đổi.
/// </summary>
public interface ILeadershipService
{
    LeaderSession? CurrentLeader { get; }

    bool IAmMaster { get; }

    /// <summary>
    /// Phát khi Master thay đổi. Engine TV1 subscribe để hủy vòng cũ.
    /// </summary>
    event Action<LeaderSession?> LeaderChanged;
}

// ─────────────────────────────────────────────
// Interface TV3 cung cấp (TV1 và TV2 sử dụng)
// ─────────────────────────────────────────────

/// <summary>
/// [TV3 triển khai] Nhận và lưu các sự kiện audit từ TV1 và TV2.
/// </summary>
public interface IAuditSink
{
    void Record(AuditEvent auditEvent);
}

