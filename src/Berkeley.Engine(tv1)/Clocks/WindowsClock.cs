using Berkeley.Contracts;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Berkeley.Engine.Clocks;

/// <summary>
/// [A4, A5] Implementation thật của ISystemClock trên Windows.
/// Đọc giờ UTC từ hệ thống và gọi SetSystemTime để áp dụng Step.
/// Yêu cầu quyền SE_SYSTEMTIME_NAME (chạy với quyền Administrator).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsClock : ISystemClock, IClockReader
{
    private static readonly Stopwatch _mono = Stopwatch.StartNew();

    // ── Đọc giờ ────────────────────────────────────────────────────────────

    public long ReadUtcMs()
        => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public long ReadMonotonicMs()
        => _mono.ElapsedMilliseconds;

    // ── Quyền ──────────────────────────────────────────────────────────────

    public bool HasSetTimePermission()
    {
        // Thử gọi GetSystemTime rồi SetSystemTime với cùng giá trị — nếu không lỗi thì có quyền.
        // Cách an toàn hơn: kiểm tra token privilege, nhưng thử thật đơn giản hơn cho đồ án.
        try
        {
            NativeMethods.GetSystemTime(out var st);
            return NativeMethods.SetSystemTime(ref st);
        }
        catch
        {
            return false;
        }
    }

    // ── Áp dụng Step ───────────────────────────────────────────────────────

    public AdjustmentResult ApplyStep(string nodeId, string roundId, double adjustmentMs)
    {
        long beforeUtcMs = ReadUtcMs();

        try
        {
            // Tính thời điểm đích
            DateTimeOffset target = DateTimeOffset.FromUnixTimeMilliseconds(
                beforeUtcMs + (long)Math.Round(adjustmentMs));

            var st = ToSystemTime(target);

            bool ok = NativeMethods.SetSystemTime(ref st);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                // Mã lỗi 1314 = ERROR_PRIVILEGE_NOT_HELD
                if (err == 1314)
                    return new AdjustmentResult(nodeId, roundId, adjustmentMs,
                        beforeUtcMs, beforeUtcMs, AdjustmentStatus.PermissionDenied,
                        $"Win32 error {err}: privilege not held");

                return new AdjustmentResult(nodeId, roundId, adjustmentMs,
                    beforeUtcMs, beforeUtcMs, AdjustmentStatus.Failed,
                    $"Win32 error {err}");
            }

            // Đọc lại giờ sau khi set để xác nhận
            long afterUtcMs = ReadUtcMs();

            return new AdjustmentResult(nodeId, roundId, adjustmentMs,
                beforeUtcMs, afterUtcMs, AdjustmentStatus.Applied);
        }
        catch (Exception ex)
        {
            return new AdjustmentResult(nodeId, roundId, adjustmentMs,
                beforeUtcMs, beforeUtcMs, AdjustmentStatus.Failed, ex.Message);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static NativeMethods.SYSTEMTIME ToSystemTime(DateTimeOffset dt)
    {
        var utc = dt.UtcDateTime;
        return new NativeMethods.SYSTEMTIME
        {
            wYear = (ushort)utc.Year,
            wMonth = (ushort)utc.Month,
            wDay = (ushort)utc.Day,
            wHour = (ushort)utc.Hour,
            wMinute = (ushort)utc.Minute,
            wSecond = (ushort)utc.Second,
            wMilliseconds = (ushort)utc.Millisecond
        };
    }
}

/// <summary>
/// P/Invoke wrappers cho Windows time API.
/// </summary>
internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEMTIME
    {
        public ushort wYear;
        public ushort wMonth;
        public ushort wDayOfWeek;
        public ushort wDay;
        public ushort wHour;
        public ushort wMinute;
        public ushort wSecond;
        public ushort wMilliseconds;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetSystemTime(ref SYSTEMTIME st);

    [DllImport("kernel32.dll")]
    public static extern void GetSystemTime(out SYSTEMTIME st);
}

