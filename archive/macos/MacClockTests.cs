using System.Runtime.InteropServices;
using Berkeley.Client.Clocks;
using Berkeley.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Berkeley.Client.Tests;

[TestClass]
public class MacClockTests
{
    private sealed class Api : IMacTimeApi
    {
        public bool IsSupported { get; set; } = true;
        public uint EffectiveUserId { get; set; }
        public int Error, Calls;
        public DarwinTimeval Last;
        public int SetTime(DarwinTimeval value) { Calls++; Last = value; return Error; }
    }
    [TestMethod]
    public void DarwinLayout_Matches64BitAbi()
    {
        Assert.AreEqual(16, Marshal.SizeOf<DarwinTimeval>());
        Assert.AreEqual(8, Marshal.OffsetOf<DarwinTimeval>(nameof(DarwinTimeval.Microseconds)).ToInt32());
    }
    [DataTestMethod]
    [DataRow(1_234L, 1L, 234000)]
    [DataRow(-1L, -1L, 999000)]
    [DataRow(-1000L, -1L, 0)]
    public void UnixMilliseconds_NormalizesMicroseconds(long value, long seconds, int microseconds)
    {
        var tv = DarwinTimeval.FromUnixMilliseconds(value);
        Assert.AreEqual(seconds, tv.Seconds); Assert.AreEqual(microseconds, tv.Microseconds);
    }
    [TestMethod]
    public void PermissionCheck_NeverCallsSetTime()
    {
        var api = new Api(); var clock = new MacClock(api);
        for (int i = 0; i < 10; i++) Assert.IsTrue(clock.HasSetTimePermission());
        Assert.AreEqual(0, api.Calls);
        api.EffectiveUserId = 501;
        Assert.IsFalse(clock.HasSetTimePermission());
        Assert.AreEqual(AdjustmentStatus.PermissionDenied, clock.ApplyStep("Mac", "r", 1000).Status);
        Assert.AreEqual(0, api.Calls);
    }
    [TestMethod]
    public void UnsupportedPlatform_DoesNotCallNativeCode()
    {
        var api = new Api { IsSupported = false };
        var clock = new MacClock(api);
        Assert.IsFalse(clock.HasSetTimePermission());
        Assert.AreEqual(AdjustmentStatus.Failed, clock.ApplyStep("Mac", "r", 1).Status);
        Assert.AreEqual(0, api.Calls);
        if (!OperatingSystem.IsMacOS()) Assert.IsFalse(new MacClock().HasSetTimePermission());
    }
    [DataTestMethod]
    [DataRow(120000.0)]
    [DataRow(-180000.0)]
    public void Step_ConvertsUtcTargetWithoutTouchingHost(double delta)
    {
        var api = new Api(); var clock = new MacClock(api);
        long before = clock.ReadUtcMs();
        var result = clock.ApplyStep("Mac", "r", delta);
        long after = clock.ReadUtcMs();
        Assert.AreEqual(AdjustmentStatus.Applied, result.Status);
        long target = api.Last.Seconds * 1000 + api.Last.Microseconds / 1000;
        Assert.IsTrue(target >= before + delta && target <= after + delta);
        Assert.AreEqual(1, api.Calls);
        Assert.AreEqual("Mac", result.NodeId); Assert.AreEqual("r", result.RoundId);
    }
    [DataTestMethod]
    [DataRow(1, AdjustmentStatus.PermissionDenied)]
    [DataRow(13, AdjustmentStatus.PermissionDenied)]
    [DataRow(22, AdjustmentStatus.Failed)]
    public void NativeError_IsReported(int error, AdjustmentStatus expected)
    {
        var api = new Api { Error = error };
        var result = new MacClock(api).ApplyStep("Mac", "r", 0);
        Assert.AreEqual(expected, result.Status);
        StringAssert.Contains(result.ErrorMessage!, error.ToString());
    }
    [DataTestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.MaxValue)]
    public void InvalidStep_NeverReachesNative(double delta)
    {
        var api = new Api(); var clock = new MacClock(api);
        Assert.AreEqual(AdjustmentStatus.Rejected, clock.ApplyStep("Mac", "r", delta).Status);
        Assert.AreEqual(0, api.Calls);
    }
    [TestMethod]
    public void StoppedClientClock_BlocksNativeStep()
    {
        var api = new Api(); var client = new ClientClock(new MacClock(api));
        Assert.AreEqual(AdjustmentStatus.Rejected, client.ApplyStep("Mac", "r", 1000).Status);
        client.AllowAdjustments = true;
        Assert.AreEqual(AdjustmentStatus.Applied, client.ApplyStep("Mac", "r", 1000).Status);
        client.AllowAdjustments = false;
        Assert.AreEqual(AdjustmentStatus.Rejected, client.ApplyStep("Mac", "r", 1000).Status);
        Assert.AreEqual(1, api.Calls);
    }
    [TestMethod]
    public void DemoClock_IsIndependentOfSystemTime()
    {
        var demo = new ClientClock { AllowAdjustments = true };
        Assert.AreEqual(AdjustmentStatus.Applied, demo.ApplyStep("Mac", "r", 120000).Status);
        Assert.IsTrue(Math.Abs(demo.ReadUtcMs() - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 120000) < 100);
    }
    [TestMethod]
    public void ActualMacPermissionProbe_IsReadOnly()
    {
        if (!OperatingSystem.IsMacOS()) { Assert.Inconclusive("Native permission probe requires macOS."); return; }
        var clock = new MacClock();
        _ = clock.HasSetTimePermission();
        Assert.IsTrue(Math.Abs(clock.ReadUtcMs() - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) < 100);
    }
}
