using Berkeley.Contracts;

namespace Berkeley.Engine.Synchronization;

/// <summary>
/// [A1, A2, A3] Lõi tính toán Berkeley — thuần túy hàm, không có side effect.
/// Nhận dữ liệu đo vào, trả kết quả ra. Không biết socket, GUI hay Windows API.
/// </summary>
public static class BerkeleyCalculator
{
    // ────────────────────────────────────────────────────────────────────────
    // A2: Tính offset từ 4 mốc t1-t4
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ước lượng offset của Worker so với Master từ 4 mốc thời gian.
    /// Offset > 0: Worker nhanh hơn Master.
    /// Offset < 0: Worker chậm hơn Master.
    ///
    /// Công thức: d = ((t2 - t1) + (t3 - t4)) / 2
    /// RTT ước lượng: (t4 - t1) - (t3 - t2)
    /// </summary>
    public static (double OffsetMs, double RttMs) ComputeOffset(
        long t1MasterSendUtcMs,
        long t2WorkerRecvUtcMs,
        long t3WorkerSendUtcMs,
        long t4MasterRecvUtcMs)
    {
        double rtt = (t4MasterRecvUtcMs - t1MasterSendUtcMs)
                   - (t3WorkerSendUtcMs - t2WorkerRecvUtcMs);
        double offset = ((t2WorkerRecvUtcMs - t1MasterSendUtcMs)
                       + (t3WorkerSendUtcMs - t4MasterRecvUtcMs)) / 2.0;
        return (offset, rtt);
    }

    // ────────────────────────────────────────────────────────────────────────
    // A3: Tính trung bình và mức hiệu chỉnh cho từng Node
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tính trung bình các offset hợp lệ (gồm cả Master với offset = 0)
    /// và trả về mức hiệu chỉnh cho từng Node.
    ///
    /// a_i = d_avg - d_i
    /// Master: d_M = 0 → a_M = d_avg
    ///
    /// Trả về null nếu không có mẫu hợp lệ nào.
    /// </summary>
    public static BerkeleyResult? ComputeAdjustments(
        string masterNodeId,
        IReadOnlyList<ClockSample> validSamples)
    {
        if (validSamples.Count == 0)
            return null;

        // d_avg bao gồm Master (offset = 0) + tất cả Worker hợp lệ
        int totalCount = validSamples.Count + 1; // +1 cho Master
        double sumOffsets = validSamples.Sum(s => s.OffsetMs); // Master có offset = 0
        double avgOffset = sumOffsets / totalCount;

        // Tính mức chỉnh cho từng Worker
        var workerAdjustments = validSamples.Select(s => new NodeAdjustment(
            NodeId: s.WorkerNodeId,
            OffsetMs: s.OffsetMs,
            AdjustmentMs: avgOffset - s.OffsetMs
        )).ToList();

        // Master tự chỉnh: a_M = d_avg - 0 = d_avg
        var masterAdjustment = new NodeAdjustment(
            NodeId: masterNodeId,
            OffsetMs: 0,
            AdjustmentMs: avgOffset
        );

        return new BerkeleyResult(
            AverageOffsetMs: avgOffset,
            MasterAdjustment: masterAdjustment,
            WorkerAdjustments: workerAdjustments
        );
    }

    // ────────────────────────────────────────────────────────────────────────
    // Kiểm tra mẫu hợp lệ
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Kiểm tra mẫu có hợp lệ để đưa vào tính toán không.
    /// Trả về (true, null) nếu hợp lệ; (false, lý do) nếu bị loại.
    /// </summary>
    public static (bool IsValid, string? Reason) ValidateSample(
        ClockSample sample,
        string expectedRoundId,
        long nowMonotonicMs,
        long sampleMaxAgeMs,
        long sampleTakenMonotonicMs,
        double maxRttMs)
    {
        if (sample.RoundId != expectedRoundId)
            return (false, "Stale round ID");

        long ageMs = nowMonotonicMs - sampleTakenMonotonicMs;
        if (ageMs > sampleMaxAgeMs)
            return (false, $"Sample too old: {ageMs}ms > {sampleMaxAgeMs}ms");

        if (sample.RttMs < 0)
            return (false, $"Negative RTT: {sample.RttMs}ms");

        if (sample.RttMs > maxRttMs)
            return (false, $"RTT too high: {sample.RttMs}ms > {maxRttMs}ms");

        return (true, null);
    }

    /// <summary>
    /// Đo lại độ trải (spread) giữa các đồng hồ sau khi hiệu chỉnh.
    /// E = max(C_i) - min(C_i) trong tập mẫu đo lại.
    /// </summary>
    public static double ComputeSpread(IReadOnlyList<double> remeasuredOffsetsMs)
    {
        if (remeasuredOffsetsMs.Count == 0) return 0;
        return remeasuredOffsetsMs.Max() - remeasuredOffsetsMs.Min();
    }
}

/// <summary>Kết quả tính toán Berkeley cho một vòng.</summary>
public record BerkeleyResult(
    double AverageOffsetMs,
    NodeAdjustment MasterAdjustment,
    List<NodeAdjustment> WorkerAdjustments
);

/// <summary>Mức hiệu chỉnh cho một Node.</summary>
public record NodeAdjustment(
    string NodeId,
    double OffsetMs,
    double AdjustmentMs
);

