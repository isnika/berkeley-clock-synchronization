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

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task R01_OldMasterRejoins_AutomaticallyAgreesAndSyncs(bool recreate, bool defaultTimeouts)
    {
        int port1 = NetworkingTestHelpers.FreePort(), port2;
        do { port2 = NetworkingTestHelpers.FreePort(); } while (port1 == port2);
        var config1 = Config("Node1", port1, [("Node2", port2)]);
        var config2 = Config("Node2", port2, [("Node1", port1)]);
        if (defaultTimeouts)
        {
            config1.Node = config1.Node with { OperationParams = new NodeOperationParams() };
            config2.Node = config2.Node with { OperationParams = new NodeOperationParams() };
        }
        var clock1 = new TestClock(1_700_000_120_000);
        var clock2 = new TestClock(1_699_999_820_000);
        await using var survivor = new ClientSession(config1, Path.Combine(_directory, "survivor"), _ => new ClientClock(clock1));
        var returning = NewMaster();
        try
        {
            await survivor.StartAsync();
            await returning.StartAsync();
            await WaitForAgreement();
            for (int cycle = 0; cycle < (defaultTimeouts ? 1 : 3); cycle++)
            {
                long previous = survivor.Snapshot()!.LeaderSessionId!.Value;
                await returning.StopAsync();
                await NetworkingTestHelpers.WaitUntilAsync(() => !survivor.IsElecting &&
                    survivor.Snapshot()?.CurrentMasterId == "Node1" && survivor.Snapshot()!.LeaderSessionId > previous,
                    TimeSpan.FromSeconds(25), "Survivor did not take over.");
                long failover = survivor.Snapshot()!.LeaderSessionId!.Value;
                if (recreate) { await returning.DisposeAsync(); returning = NewMaster(); }
                await returning.StartAsync();
                await WaitForAgreement(); // No ElectAsync or operator intervention.
                Assert.IsTrue(survivor.Snapshot()!.LeaderSessionId > failover, "Rejoin must open a newer session.");
                RoundSummary? summary = null;
                void Completed(RoundSummary value) => summary = value;
                returning.RoundCompleted += Completed;
                Assert.IsFalse(await survivor.SyncAsync(), "Former survivor must now be a Worker.");
                Assert.IsTrue(await returning.SyncAsync());
                returning.RoundCompleted -= Completed;
                Assert.AreEqual(RoundOutcome.Success, summary?.Outcome);
                Assert.AreEqual(1, summary!.AcceptedSamples.Count);
                Assert.AreEqual(2, summary.AdjustmentResults.Count);
                Assert.IsTrue(summary.AdjustmentResults.All(result => result.Status == AdjustmentStatus.Applied));
                Assert.IsTrue(summary.RemeasuredSpreadMs is >= 0 and < 100);
                Console.WriteLine($"R01 cycle={cycle + 1} recreate={recreate} defaults={defaultTimeouts}: " +
                    $"failover={failover}, rejoin={survivor.Snapshot()!.LeaderSessionId}, master=Node2, Applied=2");
            }
            // A lower-priority Worker also learns the existing, newer session after restarting.
            long stable = returning.Snapshot()!.LeaderSessionId!.Value;
            await survivor.StopAsync();
            await survivor.StartAsync();
            await WaitForAgreement();
            Assert.AreEqual(stable, survivor.Snapshot()!.LeaderSessionId);
        }
        finally { await returning.DisposeAsync(); }

        ClientSession NewMaster() => new(config2, Path.Combine(_directory, "returning"), _ => new ClientClock(clock2));
        Task WaitForAgreement() => NetworkingTestHelpers.WaitUntilAsync(() =>
            !survivor.IsElecting && !returning.IsElecting &&
            survivor.Snapshot()?.CurrentMasterId == "Node2" && returning.Snapshot()?.CurrentMasterId == "Node2" &&
            survivor.Snapshot()!.LeaderSessionId == returning.Snapshot()!.LeaderSessionId &&
            survivor.Snapshot()!.KnownPeers.All(peer => peer.ConnectionState == PeerConnectionState.Connected) &&
            returning.Snapshot()!.KnownPeers.All(peer => peer.ConnectionState == PeerConnectionState.Connected),
            TimeSpan.FromSeconds(15), "R01: nodes online but disagree on Master/session after rejoin.");
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
            await NetworkingTestHelpers.WaitUntilAsync(() => sessions.Take(2).All(s => !s.IsElecting &&
                s.Snapshot()?.CurrentMasterId == "Node2" &&
                s.Snapshot()!.KnownPeers.Single(p => p.NodeId == "Node3").ConnectionState == PeerConnectionState.Disconnected) &&
                sessions[0].Snapshot()!.LeaderSessionId == sessions[1].Snapshot()!.LeaderSessionId,
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
            long failoverSession = sessions[1].Snapshot()!.LeaderSessionId!.Value;
            await sessions[2].StartAsync();
            await NetworkingTestHelpers.WaitUntilAsync(() => sessions.All(s => !s.IsElecting &&
                s.Snapshot()?.CurrentMasterId == "Node3" && s.Snapshot()!.LeaderSessionId > failoverSession &&
                s.Snapshot()!.KnownPeers.All(p => p.ConnectionState == PeerConnectionState.Connected)) &&
                sessions.Select(s => s.Snapshot()!.LeaderSessionId).Distinct().Count() == 1,
                TimeSpan.FromSeconds(15), "Three Windows clients did not agree after Node3 returned.");
            sessions[2].RoundCompleted += value => summary = value;
            Assert.IsTrue(await sessions[2].SyncAsync());
            Assert.AreEqual(RoundOutcome.Success, summary!.Outcome);
            Assert.AreEqual(2, summary.AcceptedSamples.Count);
            Assert.AreEqual(3, summary.AdjustmentResults.Count);
            Assert.IsTrue(summary.AdjustmentResults.All(result => result.Status == AdjustmentStatus.Applied));
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
        // This verifies six-node synchronization, not a 100 ms heartbeat stress
        // workload on a shared CI VM. Use the production timing configuration.
        var configs = endpoints.Select(item => Config(item.Item1, item.port,
            endpoints.Where(peer => peer.Item1 != item.Item1))).ToArray();
        foreach (var config in configs)
            config.Node = config.Node with { OperationParams = new NodeOperationParams() };
        var sessions = endpoints.Select((item, i) => new ClientSession(
            configs[i],
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

    [TestMethod]
    public async Task AutoSync_Periodic_Rejoin_WorkerAndMaster_ManualAndStop()
    {
        int p1 = NetworkingTestHelpers.FreePort(), p2;
        do { p2 = NetworkingTestHelpers.FreePort(); } while (p1 == p2);
        var c1 = Config("Node1", p1, [("Node2", p2)]);
        var c2 = Config("Node2", p2, [("Node1", p1)]);
        c1.AutoSyncEnabled = c2.AutoSyncEnabled = new AppConfig().AutoSyncEnabled;
        Assert.IsTrue(c1.AutoSyncEnabled, "New configurations must enable AutoSync.");
        c1.AutoSyncIntervalSeconds = c2.AutoSyncIntervalSeconds = 2;
        var clock1 = new TestClock(1_700_000_120_000);
        var clock2 = new TestClock();
        await using var a = new ClientSession(c1, Path.Combine(_directory, "a"), _ => new ClientClock(clock1));
        await using var b = new ClientSession(c2, Path.Combine(_directory, "b"), _ => new ClientClock(clock2));
        var rounds = new System.Collections.Concurrent.ConcurrentQueue<RoundSummary>();
        b.RoundCompleted += rounds.Enqueue;
        await a.StartAsync(); await b.StartAsync();
        await WaitRounds(2); // Startup agreement, then a periodic round, without any manual call.
        Assert.IsTrue(Math.Abs(clock1.ReadUtcMs() - clock2.ReadUtcMs()) < 100);
        await a.StopAsync();
        clock1.ApplyStep("Node1", "test-drift", -180000);
        int before = rounds.Count;
        await a.StartAsync();
        await WaitRounds(before + 1);
        Assert.IsTrue(Math.Abs(clock1.ReadUtcMs() - clock2.ReadUtcMs()) < 100);
        await b.StopAsync();
        await NetworkingTestHelpers.WaitUntilAsync(() => !a.IsElecting && a.Snapshot()?.CurrentMasterId == "Node1",
            TimeSpan.FromSeconds(15), "Survivor did not become Master.");
        long failover = a.Snapshot()!.LeaderSessionId!.Value;
        before = rounds.Count;
        clock2.ApplyStep("Node2", "test-drift", 120000);
        await b.StartAsync();
        await WaitRounds(before + 1);
        Assert.AreEqual(a.Snapshot()!.LeaderSessionId, b.Snapshot()!.LeaderSessionId);
        Assert.IsTrue(b.Snapshot()!.LeaderSessionId > failover);
        Assert.IsTrue(Math.Abs(clock1.ReadUtcMs() - clock2.ReadUtcMs()) < 100);
        var manual = b.SyncAsync();
        Assert.IsFalse(await b.SyncAsync(), "Concurrent requests must not queue overlapping rounds.");
        Assert.IsTrue(await manual, "Manual sync must remain available between automatic rounds.");
        await a.StopAsync(); await b.StopAsync();
        before = rounds.Count;
        await Task.Delay(2300);
        Assert.AreEqual(before, rounds.Count, "Stopped scheduler produced another round.");
        Assert.IsTrue(rounds.All(r => r.Outcome == RoundOutcome.Success && r.AcceptedSamples.Count == 1 &&
            r.AdjustmentResults.Count == 2 && r.AdjustmentResults.All(s => s.Status == AdjustmentStatus.Applied)));
        await b.Audit.FlushAsync();
        Assert.AreEqual(rounds.Count, (await b.Audit.GetRecentRoundsAsync()).Count);

        Task WaitRounds(int count) => NetworkingTestHelpers.WaitUntilAsync(() => rounds.Count >= count,
            TimeSpan.FromSeconds(15), "AutoSync did not run after agreement/periodically/rejoin.");
    }

    [TestMethod]
    public async Task AutoSync_RequiresPeerAgreement_ManualStillWorksWithOlderPeer()
    {
        int p1 = NetworkingTestHelpers.FreePort(), p2;
        do { p2 = NetworkingTestHelpers.FreePort(); } while (p1 == p2);
        var config = Config("Node2", p2, [("Node1", p1)]);
        config.AutoSyncEnabled = true; config.AutoSyncIntervalSeconds = 1;
        var oldConfig = Config("Node1", p1, [("Node2", p2)]);
        var clock = new TestClock();
        var audit = new TestAuditSink();
        await using var transport = new Berkeley.Networking.TcpNetworkTransport(oldConfig.Node, clock, audit);
        await using var leadership = new Berkeley.Networking.BullyLeadershipService(oldConfig.Node, transport, clock, audit);
        using var oldNode = new Berkeley.Engine.Runtime.NodeApplication(clock, transport, leadership, audit);
        await oldNode.StartAsync(oldConfig.Node);
        await using var master = new ClientSession(config, _directory, _ => new ClientClock(new TestClock()));
        int rounds = 0; master.RoundCompleted += _ => Interlocked.Increment(ref rounds);
        await master.StartAsync();
        await NetworkingTestHelpers.WaitUntilAsync(() => master.Snapshot()!.KnownPeers.All(p => p.ConnectionState == PeerConnectionState.Connected),
            TimeSpan.FromSeconds(5), "Peer did not connect.");
        await Task.Delay(2200);
        Assert.AreEqual(0, rounds, "Online alone must not enable automatic clock steps.");
        Assert.IsTrue(await master.SyncAsync());
        Assert.AreEqual(1, rounds);
    }

    [TestMethod]
    public async Task AutoSync_ViewModelSavesAndReloadsSettings()
    {
        await using (var vm = new ClientViewModel(_directory, new Dialogs()))
        {
            await vm.InitializeAsync();
            vm.ConfigAutoSync = true; vm.ConfigAutoSyncInterval = "7";
            await vm.SaveConfigCommand.ExecuteAsync(null);
            var saved = AppConfigManager.Load(Path.Combine(_directory, "node_config.json"));
            Assert.IsTrue(saved.AutoSyncEnabled);
            Assert.AreEqual(7, saved.AutoSyncIntervalSeconds);
        }
        await using var reopened = new ClientViewModel(_directory, new Dialogs());
        await reopened.InitializeAsync();
        Assert.IsTrue(reopened.ConfigAutoSync);
        Assert.AreEqual("7", reopened.ConfigAutoSyncInterval);
    }

    [TestMethod]
    public void AutoSync_DefaultAndSavedChoiceArePreserved()
    {
        var path = Path.Combine(_directory, "node_config.json");
        Assert.IsTrue(AppConfigManager.Load(path).AutoSyncEnabled);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "{}");
        Assert.IsTrue(AppConfigManager.Load(path).AutoSyncEnabled, "Missing setting uses the new default.");
        var config = new AppConfig { AutoSyncEnabled = false };
        AppConfigManager.Save(config, path);
        Assert.IsFalse(AppConfigManager.Load(path).AutoSyncEnabled, "An explicit saved choice must be respected.");
        config.AutoSyncEnabled = true;
        AppConfigManager.Save(config, path);
        Assert.IsTrue(AppConfigManager.Load(path).AutoSyncEnabled);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(86401)]
    public void AutoSync_InvalidIntervalIsRejected(int interval)
    {
        var config = new AppConfig { AutoSyncIntervalSeconds = interval };
        Assert.ThrowsException<ArgumentException>(() => ConfigValidator.Validate(config));
    }

    private static AppConfig Config(string id, int port, IEnumerable<(string, int)> peers)
    {
        var node = NetworkingTestHelpers.Config(id, port, peers, 100, 1500, 400);
        return new AppConfig
        {
            AutoSyncEnabled = false, // These scenarios explicitly exercise manual rounds.
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
