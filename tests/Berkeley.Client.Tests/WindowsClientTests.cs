using Berkeley.Client.Clocks;
using Berkeley.Contracts;
using Berkeley.Desktop.Configuration;
using Berkeley.Networking.Tests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Berkeley.Client.Tests;

[TestClass]
public class WindowsClientTests
{
    private string _directory = null!;
    [TestInitialize] public void Setup() => _directory = Path.Combine(Path.GetTempPath(), "berkeley-windows-" + Guid.NewGuid());
    [TestCleanup] public void Cleanup()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [TestMethod]
    public void WindowsFactory_ReadsRealClockAndTokenWithoutChangingSystemTime()
    {
        var clock = ClientClock.Create(simulated: false);
        Assert.IsFalse(clock.Simulated);
        Assert.IsTrue(Math.Abs(clock.ReadUtcMs() - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) < 1000);
        _ = clock.HasSetTimePermission(); // Token-only probe; never calls SetSystemTime.
        Assert.AreEqual(AdjustmentStatus.Rejected, clock.ApplyStep("Node1", "stopped", 120000).Status);
    }

    [TestMethod]
    public void StoppingClientClock_BlocksFurtherAdjustments()
    {
        var underlying = new TestClock();
        var clock = new ClientClock(underlying);
        Assert.AreEqual(AdjustmentStatus.Rejected, clock.ApplyStep("Node1", "r", 120000).Status);
        clock.AllowAdjustments = true;
        Assert.AreEqual(AdjustmentStatus.Applied, clock.ApplyStep("Node1", "r", 120000).Status);
        clock.AllowAdjustments = false;
        Assert.AreEqual(AdjustmentStatus.Rejected, clock.ApplyStep("Node1", "r", -180000).Status);
        Assert.IsTrue(Math.Abs(underlying.ReadUtcMs() - 1_700_000_120_000) < 1000);
    }

    [TestMethod]
    public void DemoAndInvalidAdjustments_DoNotChangeSystemClock()
    {
        var clock = ClientClock.Create(simulated: true);
        clock.AllowAdjustments = true;
        Assert.IsTrue(clock.Simulated);
        Assert.AreEqual(AdjustmentStatus.Applied, clock.ApplyStep("Node1", "demo", 120000).Status);
        Assert.IsTrue(Math.Abs(clock.ReadUtcMs() - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 120000) < 1000);
        Assert.AreEqual(AdjustmentStatus.Rejected, clock.ApplyStep("Node1", "bad", double.NaN).Status);
        Assert.AreEqual(AdjustmentStatus.Rejected, clock.ApplyStep("Node1", "bad", double.MaxValue).Status);
    }

    [DataTestMethod]
    [DataRow(120000L)]
    [DataRow(-180000L)]
    public async Task TwoWindowsViewModels_RealTcp_SyncStepHistoryAndRestart(long skew)
    {
        int port1 = NetworkingTestHelpers.FreePort(), port2;
        do { port2 = NetworkingTestHelpers.FreePort(); } while (port1 == port2);
        var dir1 = Path.Combine(_directory, "Node1");
        var dir2 = Path.Combine(_directory, "Node2");
        AppConfigManager.Save(Config("Node1", port1, [("Node2", port2)]), Path.Combine(dir1, "node_config.json"));
        AppConfigManager.Save(Config("Node2", port2, [("Node1", port1)]), Path.Combine(dir2, "node_config.json"));
        var clock1 = new TestClock(1_700_000_000_000 + skew);
        var clock2 = new TestClock();
        var dialogs = new Dialogs { SavePath = Path.Combine(_directory, "history.csv") };
        await using var worker = new ClientViewModel(dir1, dialogs, _ => new ClientClock(clock1));
        await using var master = new ClientViewModel(dir2, dialogs, _ => new ClientClock(clock2));
        await worker.InitializeAsync(); await master.InitializeAsync();
        await WaitForCluster();
        StringAssert.Contains(master.Mode, "TCP THẬT");
        StringAssert.Contains(worker.OperatorHint, "Worker");
        Assert.IsFalse(worker.SyncCommand.CanExecute(null));
        Assert.IsTrue(master.SyncCommand.CanExecute(null));
        await master.SyncCommand.ExecuteAsync(null);
        Pump();
        Assert.IsTrue(Math.Abs(clock1.ReadUtcMs() - clock2.ReadUtcMs()) < 100);
        Assert.AreEqual(2, master.Samples.Count);
        Assert.IsTrue(master.Samples.All(row => row.Step == "Applied"));
        Assert.AreEqual("Applied", worker.Nodes.Single(row => row.NodeId == "Node1").Step);
        Assert.AreEqual(1, master.History.Count);
        Assert.IsTrue(master.ResultVisible);
        await master.ExportHistoryCommand.ExecuteAsync(null);
        Assert.AreEqual(2, File.ReadAllLines(dialogs.SavePath!).Length);
        await worker.StopCommand.ExecuteAsync(null);
        Assert.IsFalse(worker.IsRunning);
        await worker.StartCommand.ExecuteAsync(null);
        await WaitForCluster();
        await master.SyncCommand.ExecuteAsync(null);
        Pump();
        Assert.AreEqual(2, master.History.Count);
        Assert.IsTrue(master.Samples.All(row => row.Step == "Applied"));

        void Pump() { for (int i = 0; i < 8; i++) { worker.Tick(); master.Tick(); } }
        Task WaitForCluster() => NetworkingTestHelpers.WaitUntilAsync(() =>
        {
            Pump();
            return master.IsMaster && !worker.IsMaster && master.Connected == "2/2 máy online"
                && worker.Connected == "2/2 máy online" && master.Master == worker.Master
                && master.SyncCommand.CanExecute(null);
        }, TimeSpan.FromSeconds(10), "Windows clients did not agree on Master and connect.");
    }

    [TestMethod]
    public async Task DemoElection_ShowsOperatorStatusAndCanBeCancelled()
    {
        await using var vm = new ClientViewModel(_directory, new Dialogs());
        await vm.InitializeAsync();
        var election = vm.ElectCommand.ExecuteAsync(null);
        vm.Tick();
        Assert.IsTrue(vm.IsElecting);
        StringAssert.Contains(vm.OperatorHint, "bầu chọn");
        Assert.IsFalse(vm.SyncCommand.CanExecute(null));
        await vm.StopCommand.ExecuteAsync(null);
        await election;
        for (int i = 0; i < 8; i++) vm.Tick();
        Assert.IsFalse(vm.IsElecting);
        Assert.IsFalse(vm.IsRunning);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.IsTrue(vm.IsRunning);
    }

    [TestMethod]
    public async Task ThreeWindowsClients_LoseMaster_ElectAndContinueSyncing()
    {
        var ports = new HashSet<int>();
        while (ports.Count < 3) ports.Add(NetworkingTestHelpers.FreePort());
        var endpoints = ports.Select((port, index) => ($"Node{index + 1}", port)).ToArray();
        var clocks = new[] { new TestClock(1_700_000_120_000), new TestClock(1_699_999_820_000), new TestClock() };
        var sessions = endpoints.Select((item, i) => new ClientSession(
            Config(item.Item1, item.port, endpoints.Where(peer => peer.Item1 != item.Item1)),
            Path.Combine(_directory, item.Item1), _ => new ClientClock(clocks[i]))).ToArray();
        try
        {
            foreach (var session in sessions) await session.StartAsync();
            await NetworkingTestHelpers.WaitUntilAsync(() => sessions.All(s =>
                s.Snapshot()?.CurrentMasterId == "Node3" && s.Snapshot()!.KnownPeers.Count(p => p.ConnectionState == PeerConnectionState.Connected) == 2)
                && sessions.Select(s => s.Snapshot()!.LeaderSessionId).Distinct().Count() == 1,
                TimeSpan.FromSeconds(10), "Three Windows clients did not form a cluster.");
            await sessions[2].StopAsync();
            await NetworkingTestHelpers.WaitUntilAsync(() => sessions.Take(2).All(s =>
                s.Snapshot()?.CurrentMasterId == "Node2") && sessions[0].Snapshot()!.LeaderSessionId == sessions[1].Snapshot()!.LeaderSessionId,
                TimeSpan.FromSeconds(10), "Windows clients did not elect Node2 after losing Node3.");
            RoundSummary? summary = null;
            sessions[1].RoundCompleted += value => summary = value;
            Assert.IsTrue(await sessions[1].SyncAsync());
            Assert.AreEqual(RoundOutcome.Success, summary?.Outcome);
            Assert.AreEqual(1, summary!.AcceptedSamples.Count);
            Assert.AreEqual(2, summary.AdjustmentResults.Count);
            Assert.IsTrue(summary.AdjustmentResults.All(result => result.Status == AdjustmentStatus.Applied));
            Assert.IsTrue(summary.RemeasuredSpreadMs is >= 0 and < 100);
            Assert.IsTrue(Math.Abs(clocks[0].ReadUtcMs() - clocks[1].ReadUtcMs()) < 100);
        }
        finally { foreach (var session in sessions.Reverse()) await session.DisposeAsync(); }
    }

    [TestMethod]
    public async Task SixWindowsClients_RealTcp_TwoCompleteRoundsConvergeAndPersist()
    {
        var ports = new HashSet<int>();
        while (ports.Count < 6) ports.Add(NetworkingTestHelpers.FreePort());
        var endpoints = ports.Select((port, index) => ($"Node{index + 1}", port)).ToArray();
        long[] skews = [120000, -180000, 90000, -90000, 210000, 0];
        var clocks = skews.Select(skew => new TestClock(1_700_000_000_000 + skew)).ToArray();
        var sessions = endpoints.Select((item, i) => new ClientSession(
            Config(item.Item1, item.port, endpoints.Where(peer => peer.Item1 != item.Item1)),
            Path.Combine(_directory, item.Item1), _ => new ClientClock(clocks[i]))).ToArray();
        try
        {
            foreach (var session in sessions) await session.StartAsync();
            await NetworkingTestHelpers.WaitUntilAsync(() => sessions.All(s => !s.IsElecting &&
                s.Snapshot()?.CurrentMasterId == "Node6" &&
                s.Snapshot()!.KnownPeers.Count(p => p.ConnectionState == PeerConnectionState.Connected) == 5)
                && sessions.Select(s => s.Snapshot()!.LeaderSessionId).Distinct().Count() == 1,
                TimeSpan.FromSeconds(15), "Six Windows clients did not connect and agree on Node6.");

            var rounds = new List<RoundSummary>();
            sessions[5].RoundCompleted += rounds.Add;
            for (int i = 0; i < 2; i++)
            {
                Assert.IsTrue(await sessions[5].SyncAsync());
                var summary = rounds[i];
                Assert.AreEqual(RoundOutcome.Success, summary.Outcome);
                Assert.AreEqual(5, summary.AcceptedSamples.Count);
                Assert.AreEqual(0, summary.RejectedSamples.Count);
                Assert.AreEqual(6, summary.AdjustmentResults.Count);
                Assert.IsTrue(summary.AdjustmentResults.All(result => result.Status == AdjustmentStatus.Applied));
                Assert.IsTrue(summary.RemeasuredSpreadMs is >= 0 and < 100);
                var times = clocks.Select(clock => clock.ReadUtcMs()).ToArray();
                Assert.IsTrue(times.Max() - times.Min() < 100, "Six clocks failed to converge.");
            }
            await sessions[5].Audit.FlushAsync();
            Assert.AreEqual(2, (await sessions[5].Audit.GetRecentRoundsAsync()).Count);
            string export = Path.Combine(_directory, "six-clients.csv");
            await sessions[5].Audit.ExportRoundsToCsvAsync(export);
            Assert.AreEqual(3, File.ReadAllLines(export).Length);
        }
        finally { foreach (var session in sessions.Reverse()) await session.DisposeAsync(); }
    }

    private static AppConfig Config(string id, int port, IEnumerable<(string, int)> peers)
    {
        var node = NetworkingTestHelpers.Config(id, port, peers, 100, 1500, 400);
        return new AppConfig
        {
            UseSimulatedCluster = false,
            Node = node with { OperationParams = node.OperationParams with { SampleTimeoutMs = 1000, SampleMaxAgeMs = 5000, RoundTimeoutMs = 10000 } }
        };
    }
    private sealed class Dialogs : IClientDialogs
    {
        public string? SavePath;
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
        public Task<string?> SaveCsvAsync(string name) => Task.FromResult(SavePath);
    }
}
