using Berkeley.Contracts;
using System.Diagnostics;

namespace Berkeley.Engine.Clocks;

/// <summary>
/// [A4] Đồng hồ giả dùng trong unit test — không chạm giờ hệ điều hành.
/// Cho phép test kiểm soát hoàn toàn giờ và monotonic.
/// </summary>
public sealed class FakeClock : ISystemClock, IClockReader
{
    private long _utcMs;
    private long _monoMs;
    private bool _hasPermission;
    private bool _failNextStep;
    private string? _failMessage;

    public FakeClock(long initialUtcMs = 0, bool hasPermission = true)
    {
        _utcMs = initialUtcMs;
        _monoMs = 0;
        _hasPermission = hasPermission;
    }

    // ── Điều khiển từ test ─────────────────────────────────────────────────

    /// <summary>Đặt giờ UTC (ms) — mô phỏng đồng hồ của máy này.</summary>
    public void SetUtcMs(long utcMs) => _utcMs = utcMs;

    /// <summary>Tiến monotonic thêm deltaMs ms.</summary>
    public void AdvanceMonotonic(long deltaMs) => _monoMs += deltaMs;

    /// <summary>Mô phỏng thiếu quyền chỉnh giờ.</summary>
    public void SetPermission(bool hasPermission) => _hasPermission = hasPermission;

    /// <summary>Khiến lần ApplyStep kế tiếp trả về lỗi (Failed).</summary>
    public void SimulateNextStepFail(string message = "Simulated API failure")
    {
        _failNextStep = true;
        _failMessage = message;
    }

    // Lịch sử các lần ApplyStep để test kiểm tra
    public List<(double AdjustmentMs, AdjustmentStatus Status)> ApplyHistory { get; } = [];

    // ── ISystemClock ───────────────────────────────────────────────────────

    public long ReadUtcMs() => _utcMs;
    public long ReadMonotonicMs() => _monoMs;
    public bool HasSetTimePermission() => _hasPermission;

    public AdjustmentResult ApplyStep(string nodeId, string roundId, double adjustmentMs)
    {
        long before = _utcMs;

        if (!_hasPermission)
        {
            ApplyHistory.Add((adjustmentMs, AdjustmentStatus.PermissionDenied));
            return new AdjustmentResult(nodeId, roundId, adjustmentMs,
                before, before, AdjustmentStatus.PermissionDenied, "No permission (fake)");
        }

        if (_failNextStep)
        {
            _failNextStep = false;
            ApplyHistory.Add((adjustmentMs, AdjustmentStatus.Failed));
            return new AdjustmentResult(nodeId, roundId, adjustmentMs,
                before, before, AdjustmentStatus.Failed, _failMessage);
        }

        _utcMs += (long)Math.Round(adjustmentMs);
        ApplyHistory.Add((adjustmentMs, AdjustmentStatus.Applied));
        return new AdjustmentResult(nodeId, roundId, adjustmentMs,
            before, _utcMs, AdjustmentStatus.Applied);
    }
}

