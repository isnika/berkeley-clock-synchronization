using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Berkeley.Contracts;
using Berkeley.Engine.Clocks;

namespace Berkeley.Client.Clocks;

/// <summary>Serializes clock changes against Stop and supplies an isolated demo clock.</summary>
public sealed class ClientClock : ISystemClock, IClockReader
{
    private readonly ISystemClock? _real;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly long _origin = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private long _offset;
    private readonly object _gate = new();
    private bool _allow;
    public bool Simulated => _real is null;
    public bool AllowAdjustments { get { lock (_gate) return _allow; } set { lock (_gate) _allow = value; } }
    public ClientClock(ISystemClock? real = null) => _real = real;
    public static ClientClock Create(bool simulated)
    {
        if (simulated) return new();
        if (OperatingSystem.IsWindows()) return new(new WindowsClientClock());
        throw new PlatformNotSupportedException("Real clock changes require Windows.");
    }
    public long ReadUtcMs() => _real?.ReadUtcMs() ?? _origin + _elapsed.ElapsedMilliseconds + Interlocked.Read(ref _offset);
    public long ReadMonotonicMs() => _elapsed.ElapsedMilliseconds;
    public bool HasSetTimePermission() => _real?.HasSetTimePermission() ?? true;
    public AdjustmentResult ApplyStep(string nodeId, string roundId, double adjustmentMs)
    {
        lock (_gate)
        {
            long before = ReadUtcMs();
            if (!_allow || !double.IsFinite(adjustmentMs)) return new(nodeId, roundId, adjustmentMs, before, before, AdjustmentStatus.Rejected, "Node stopped or invalid adjustment.");
            if (_real != null) return _real.ApplyStep(nodeId, roundId, adjustmentMs);
            try
            {
                long updated = checked(_offset + checked((long)Math.Round(adjustmentMs)));
                _ = DateTimeOffset.FromUnixTimeMilliseconds(checked(_origin + _elapsed.ElapsedMilliseconds + updated));
                Interlocked.Exchange(ref _offset, updated);
                return new(nodeId, roundId, adjustmentMs, before, ReadUtcMs(), AdjustmentStatus.Applied);
            }
            catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
            { return new(nodeId, roundId, adjustmentMs, before, before, AdjustmentStatus.Rejected, "Adjustment exceeds date range."); }
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsClientClock : ISystemClock
{
    private readonly WindowsClock _clock = new();
    public long ReadUtcMs() => _clock.ReadUtcMs();
    public long ReadMonotonicMs() => _clock.ReadMonotonicMs();
    public AdjustmentResult ApplyStep(string node, string round, double delta) => _clock.ApplyStep(node, round, delta);
    public bool HasSetTimePermission()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!LookupPrivilegeValue(null, "SeSystemtimePrivilege", out long wanted)) return false;
        GetTokenInformation(identity.Token, 3, IntPtr.Zero, 0, out int size);
        if (size <= 0) return false;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(identity.Token, 3, buffer, size, out _)) return false;
            for (int i = 0, count = Marshal.ReadInt32(buffer); i < count; i++)
            {
                var entry = IntPtr.Add(buffer, 4 + i * 12);
                if (Marshal.ReadInt64(entry) == wanted && (Marshal.ReadInt32(entry, 8) & 4) == 0) return true;
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int required);
}
