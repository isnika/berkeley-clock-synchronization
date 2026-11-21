using System.Globalization;
using System.IO;
using Berkeley.Contracts;
using Berkeley.Desktop.Configuration;
using Berkeley.Desktop.Persistence;
using Berkeley.Desktop.Services;
using Berkeley.Engine.Clocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Berkeley.Desktop.Tests;
[TestClass]
public class ReliabilityTests
{
    private string _dir = null!;
    [TestInitialize] public void Setup() { _dir = Path.Combine(Path.GetTempPath(), "berkeley-tests-" + Guid.NewGuid()); Directory.CreateDirectory(_dir); }
    [TestCleanup] public void Cleanup() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true); }
    private static RoundSummary Round(double? spread) => new("r", "Node1", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], [], 1.25, [], spread, RoundOutcome.Success);
    [TestMethod] public void MissingOrInvalidMeasurementNeverPasses()
    {
        foreach (var value in new double?[] { null, double.NaN, double.PositiveInfinity, -1 }) Assert.IsFalse(Verification.MeetsThreshold(Round(value), 60));
        Assert.IsTrue(Verification.MeetsThreshold(Round(60_000), 60));
        Assert.IsFalse(Verification.MeetsThreshold(Round(60_001), 60));
        Assert.IsFalse(Verification.MeetsThreshold(Round(1) with { Outcome = RoundOutcome.PartialSuccess }, 60));
    }
    [TestMethod] public void CorruptConfigIsPreserved()
    {
        var path = Path.Combine(_dir, "config.json"); File.WriteAllText(path, "{broken");
        Assert.ThrowsException<System.Text.Json.JsonException>(() => AppConfigManager.Load(path));
        Assert.AreEqual("{broken", File.ReadAllText(path));
        File.WriteAllText(path, "{\"Node\":null}");
        Assert.ThrowsException<ArgumentException>(() => AppConfigManager.Load(path));
    }
    [TestMethod] public void ConfigValidationAndSaveFailureAreObservable()
    {
        var config = new AppConfig();
        config.Node = config.Node with { Peers = [new PeerConfig("node1", "127.0.0.1", 5000)] };
        Assert.ThrowsException<ArgumentException>(() => ConfigValidator.Validate(config));
        Assert.ThrowsException<UnauthorizedAccessException>(() => AppConfigManager.Save(new AppConfig(), _dir));
        var path = Path.Combine(_dir, "good.json"); AppConfigManager.Save(new AppConfig(), path);
        Assert.AreEqual("Node1", AppConfigManager.Load(path).Node.NodeId);
    }
    [TestMethod] public async Task DisposeFlushesAuditAndRoundWithoutDelay()
    {
        var sink = new SqliteAuditSink(Path.Combine(_dir, "a.db"));
        sink.Record(new(1, DateTimeOffset.UtcNow, 1, "Node1", AuditEventKind.MessageSent, "hello", new { Round = "r" }));
        sink.RecordRoundSummary(Round(1));
        await sink.DisposeAsync();
        Assert.AreEqual(1, (await sink.GetRecentRoundsAsync()).Count);
        Assert.IsNotNull((await sink.GetRecentEventsAsync())[0].Detail);
    }
    [TestMethod] public async Task SqliteFailureDoesNotPretendSuccess()
    {
        var path = Path.Combine(_dir, "bad.db"); var sink = new SqliteAuditSink(path);
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}")) { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DROP TABLE audit_events"; cmd.ExecuteNonQuery(); }
        sink.Record(new(1, DateTimeOffset.UtcNow, 1, "Node1", AuditEventKind.MessageSent, "fail"));
        await Assert.ThrowsExceptionAsync<IOException>(() => sink.FlushAsync());
        await Assert.ThrowsExceptionAsync<IOException>(() => sink.DisposeAsync().AsTask());
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsTrue(exclusive.Length > 0, "Failed storage must still release database handles.");
    }
    [TestMethod] public async Task CsvExportsAllRoundsWithInvariantNumbers()
    {
        await using var sink = new SqliteAuditSink(Path.Combine(_dir, "export.db"));
        for (var i = 0; i < 55; i++) sink.RecordRoundSummary(Round(1.5) with { RoundId = "r" + i });
        var old = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("vi-VN"); await sink.ExportRoundsToCsvAsync(Path.Combine(_dir, "out.csv")); }
        finally { CultureInfo.CurrentCulture = old; }
        var lines = File.ReadAllLines(Path.Combine(_dir, "out.csv")); Assert.AreEqual(56, lines.Length); StringAssert.Contains(lines[1], "\"1.25\"");
        Assert.AreEqual("\"a,\"\"b\"\"\"", SqliteAuditSink.Csv("a,\"b\""));
    }
    [TestMethod] public void RealClockTracksWindowsWithoutWriting()
    {
        var clock = new DesktopClock(false);
        Assert.IsTrue(Math.Abs(clock.ReadUtcMs() - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) < 100);
        for (int i = 0; i < 20; i++) _ = clock.HasSetTimePermission();
        Assert.IsTrue(Math.Abs(clock.ReadUtcMs() - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) < 100);
        clock.AllowAdjustments = false;
        Assert.AreEqual(AdjustmentStatus.Rejected, clock.ApplyStep("n", "r", 1000).Status);
    }
    [TestMethod] public void DemoStepDoesNotChangeWindows()
    {
        var clock = new DesktopClock(true); var systemBefore = DateTimeOffset.UtcNow;
        clock.ApplyStep("n", "r", 120_000);
        Assert.IsTrue(Math.Abs(clock.ReadUtcMs() - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 120_000) < 100);
        Assert.IsTrue((DateTimeOffset.UtcNow - systemBefore).TotalSeconds < 1);
    }
    [TestMethod] public async Task SimulatedWorkerIndependentOfMasterStep()
    {
        var clock = new FakeClock(1000000); using var transport = new SimulatedClusterTransport(clock, new NullAudit());
        async Task<long> Read()
        {
            var tcs = new TaskCompletionSource<long>();
            void Handle(MessageEnvelope m) => tcs.TrySetResult(m.DeserializePayload<TimeResponsePayload>()!.T2WorkerRecvUtcMs);
            transport.MessageReceived += Handle;
            try { await transport.SendAsync("Node2", MessageEnvelope.Create(MessageTypes.TimeRequest, "Node1", Guid.NewGuid(), "Node2", new TimeRequestPayload(clock.ReadUtcMs()), "r", 1)); return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
            finally { transport.MessageReceived -= Handle; }
        }
        var before = await Read(); clock.ApplyStep("Node1", "r", 35000); Assert.AreEqual(before, await Read());
    }
    [TestMethod] public async Task ViewModelStartsStopsAndRestartsRealTransport()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                    listener.Start(); int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
                    var config = new AppConfig { UseSimulatedCluster = false, Node = AppConfig.CreateDefaultNodeConfig("Node1", port) with { Peers = [] } };
                    var vm = new Berkeley.Desktop.ViewModels.MainViewModel(config, new SqliteAuditSink(Path.Combine(_dir, "lifecycle.db")));
                    for (int i = 0; i < 3; i++) { await vm.StopNodeCommand.ExecuteAsync(null); listener.Start(); listener.Stop(); vm.StartNodeCommand.Execute(null); }
                    await vm.CloseAsync(); listener.Start(); listener.Stop(); done.SetResult();
                }
                catch (Exception ex) { done.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background); }
            }));
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
        thread.Join(2000);
    }
    [TestMethod] public async Task DemoRoundPersistsAndCanBeStopped()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    var config = new AppConfig();
                    config.Node = config.Node with { OperationParams = config.Node.OperationParams with { SampleTimeoutMs = 150 } };
                    var sink = new SqliteAuditSink(Path.Combine(_dir, "demo.db"));
                    var vm = new Berkeley.Desktop.ViewModels.MainViewModel(config, sink);
                    vm.HasInputErrors = true; Assert.IsFalse(vm.SaveConfigAndRestartCommand.CanExecute(null));
                    await vm.TriggerSyncNowCommand.ExecuteAsync(null);
                    await sink.FlushAsync();
                    var rounds = await sink.GetRecentRoundsAsync(); Assert.AreEqual(1, rounds.Count);
                    Assert.AreEqual("Success", rounds[0].Outcome);
                    await vm.CloseAsync(); done.SetResult();
                }
                catch (Exception ex) { done.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background); }
            }));
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15)); thread.Join(2000);
    }
    private sealed class NullAudit : IAuditSink { public void Record(AuditEvent e) { } }
}
