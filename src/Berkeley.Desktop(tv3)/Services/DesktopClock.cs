using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Berkeley.Contracts;
using Berkeley.Engine.Clocks;
namespace Berkeley.Desktop.Services;
/// <summary>Permission inspection is read-only; demo time is independent of Windows.</summary>
public sealed class DesktopClock : ISystemClock, IClockReader
{
    private readonly WindowsClock _windows = new();
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly long _origin = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private long _offset;
    public bool Simulated { get; }
    private readonly object _stepLock = new();
    private bool _allowAdjustments = true;
    public bool AllowAdjustments { get { lock (_stepLock) return _allowAdjustments; } set { lock (_stepLock) _allowAdjustments = value; } }
    public DesktopClock(bool simulated) => Simulated = simulated;
    public long ReadUtcMs() => Simulated ? _origin + _elapsed.ElapsedMilliseconds + Interlocked.Read(ref _offset) : _windows.ReadUtcMs();
    public long ReadMonotonicMs() => _elapsed.ElapsedMilliseconds;
    public bool HasSetTimePermission()
    {
        if (Simulated) return true;
        using var identity = WindowsIdentity.GetCurrent();
        if (!LookupPrivilegeValue(null, "SeSystemtimePrivilege", out var wanted)) return false;
        GetTokenInformation(identity.Token, 3, IntPtr.Zero, 0, out var size);
        if (size <= 0) return false;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(identity.Token, 3, buffer, size, out _)) return false;
            int count = Marshal.ReadInt32(buffer);
            for (int i = 0; i < count; i++)
            {
                var entry = IntPtr.Add(buffer, 4 + i * 12);
                if (Marshal.ReadInt64(entry) == wanted && (Marshal.ReadInt32(entry, 8) & 4) == 0) return true;
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public AdjustmentResult ApplyStep(string nodeId, string roundId, double adjustmentMs)
    {
        lock (_stepLock)
        {
        long before = ReadUtcMs();
        if (!AllowAdjustments || !double.IsFinite(adjustmentMs)) return new(nodeId, roundId, adjustmentMs, before, before, AdjustmentStatus.Rejected, "Node stopped or invalid adjustment");
        if (!Simulated) return _windows.ApplyStep(nodeId, roundId, adjustmentMs);
        Interlocked.Add(ref _offset, checked((long)Math.Round(adjustmentMs)));
        return new(nodeId, roundId, adjustmentMs, before, ReadUtcMs(), AdjustmentStatus.Applied);
    }
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr data, int length, out int required);
}
