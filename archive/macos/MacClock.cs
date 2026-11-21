using System.Diagnostics;
using System.Runtime.InteropServices;
using Berkeley.Contracts;

namespace Berkeley.Client.Clocks;

/// <summary>Darwin's 64-bit timeval: time_t is 64-bit, suseconds_t is 32-bit.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct DarwinTimeval
{
    public long Seconds;
    public int Microseconds;

    public static DarwinTimeval FromUnixMilliseconds(long milliseconds)
    {
        long seconds = Math.DivRem(milliseconds, 1000, out long remainder);
        if (remainder < 0) { seconds--; remainder += 1000; }
        return new() { Seconds = seconds, Microseconds = (int)remainder * 1000 };
    }
}

// The native boundary is injectable so tests never need to change the host clock.
public interface IMacTimeApi
{
    bool IsSupported { get; }
    uint EffectiveUserId { get; }
    int SetTime(DarwinTimeval value); // errno, or zero on success
}

public sealed class DarwinTimeApi : IMacTimeApi
{
    public bool IsSupported => OperatingSystem.IsMacOS() && Environment.Is64BitProcess;
    public uint EffectiveUserId => IsSupported ? GetEffectiveUserId() : uint.MaxValue;
    public int SetTime(DarwinTimeval value)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("settimeofday requires 64-bit macOS.");
        return SetTimeOfDay(ref value, IntPtr.Zero) == 0 ? 0 : Marshal.GetLastPInvokeError();
    }
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "settimeofday", SetLastError = true)]
    private static extern int SetTimeOfDay(ref DarwinTimeval value, IntPtr timezone);
}

/// <summary>Real macOS clock. Never prompts, shells out or changes time during permission checks.</summary>
public sealed class MacClock(IMacTimeApi api) : ISystemClock, IClockReader
{
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    public MacClock() : this(new DarwinTimeApi()) { }
    public long ReadUtcMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long ReadMonotonicMs() => _elapsed.ElapsedMilliseconds;
    public bool HasSetTimePermission() => api.IsSupported && api.EffectiveUserId == 0;

    public AdjustmentResult ApplyStep(string nodeId, string roundId, double adjustmentMs)
    {
        long before = ReadUtcMs();
        AdjustmentResult Result(AdjustmentStatus status, string? error = null) =>
            new(nodeId, roundId, adjustmentMs, before, ReadUtcMs(), status, error);
        if (!double.IsFinite(adjustmentMs)) return Result(AdjustmentStatus.Rejected, "Adjustment must be finite.");
        if (!api.IsSupported) return Result(AdjustmentStatus.Failed, "Real macOS clock is unavailable on this platform.");
        if (!HasSetTimePermission()) return Result(AdjustmentStatus.PermissionDenied, "macOS requires effective UID 0 to change system time. See the Mac test guide.");
        try
        {
            long target = checked(before + checked((long)Math.Round(adjustmentMs)));
            _ = DateTimeOffset.FromUnixTimeMilliseconds(target); // validate the supported calendar range
            int error = api.SetTime(DarwinTimeval.FromUnixMilliseconds(target));
            return error switch
            {
                0 => Result(AdjustmentStatus.Applied),
                1 or 13 => Result(AdjustmentStatus.PermissionDenied, $"settimeofday denied (errno {error})."),
                _ => Result(AdjustmentStatus.Failed, $"settimeofday failed (errno {error}).")
            };
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
        { return Result(AdjustmentStatus.Rejected, "Adjustment exceeds the supported date range."); }
        catch (Exception ex) { return Result(AdjustmentStatus.Failed, ex.Message); }
    }
}
