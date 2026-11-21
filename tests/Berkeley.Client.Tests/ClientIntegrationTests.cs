using Berkeley.Client.Clocks;
using Berkeley.Contracts;
using Berkeley.Desktop.Configuration;
using Berkeley.Engine.Runtime;
using Berkeley.Networking;
using Berkeley.Networking.Tests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Berkeley.Client.Tests;

[TestClass]
public class ClientIntegrationTests
{
    private string _directory = null!;
    [TestInitialize] public void Setup() => _directory = Path.Combine(Path.GetTempPath(), "berkeley-portable-" + Guid.NewGuid());
    [TestCleanup] public void Cleanup()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
    [DataTestMethod]
    [DataRow(true, 120000L)]
    [DataRow(true, -180000L)]
    [DataRow(false, 120000L)]
    [DataRow(false, -180000L)]
    public async Task PortableAndExistingBootstrap_RealTcp_ConvergeInBothRoles(bool portableMaster, long skew)
    {
        string portableId = portableMaster ? "Node2" : "Node1";
        string existingId = portableMaster ? "Node1" : "Node2";
        int portablePort = NetworkingTestHelpers.FreePort();
        int existingPort;
        do { existingPort = NetworkingTestHelpers.FreePort(); } while (existingPort == portablePort);
        var portableConfig = NetworkingTestHelpers.Config(portableId, portablePort, [(existingId, existingPort)], 100, 2000, 500);
        var existingConfig = NetworkingTestHelpers.Config(existingId, existingPort, [(portableId, portablePort)], 100, 2000, 500);
        var portableClock = new TestClock(1_700_000_000_000 + skew);
        var existingClock = new TestClock();
        await using var portable = new ClientSession(new AppConfig { Node = portableConfig, UseSimulatedCluster = false },
            _directory, _ => new ClientClock(portableClock));
        var audit = new TestAuditSink();
        // This is the same TCP/Bully/NodeApplication bootstrap used by WPF.
        await using var tcp = new TcpNetworkTransport(existingConfig, existingClock, audit, autoStart: false);
        await tcp.StartAsync();
        await using var leader = new BullyLeadershipService(existingConfig, tcp, existingClock, audit);
        using var existing = new NodeApplication(existingClock, tcp, leader, audit);
        await existing.StartAsync(existingConfig);
        await portable.StartAsync();
        var rounds = new List<RoundSummary>();
        if (portableMaster) portable.RoundCompleted += rounds.Add;
        else existing.RoundCompleted += rounds.Add;
        try
        {
            await NetworkingTestHelpers.WaitUntilAsync(() => portable.Snapshot()?.KnownPeers.Single().ConnectionState == PeerConnectionState.Connected &&
                tcp.GetConnectedNodeIds().Count == 1 && portable.Snapshot()?.CurrentMasterId == "Node2" &&
                portable.Snapshot()?.LeaderSessionId == leader.CurrentLeader?.LeaderSessionId,
                TimeSpan.FromSeconds(5), "Mixed-client TCP cluster did not connect/agree.");
            for (int i = 0; i < 2; i++)
            {
                Assert.IsTrue(portableMaster ? await portable.SyncAsync() : await existing.TriggerSyncNowAsync());
                Assert.AreEqual(RoundOutcome.Success, rounds[i].Outcome);
                Assert.IsTrue(rounds[i].AdjustmentResults.All(x => x.Status == AdjustmentStatus.Applied));
                Assert.IsTrue(rounds[i].RemeasuredSpreadMs is >= 0 and < 100);
                Assert.IsTrue(Math.Abs(portableClock.ReadUtcMs() - existingClock.ReadUtcMs()) < 100);
            }
            await portable.Audit.FlushAsync();
            Assert.AreEqual(portableMaster ? 2 : 0, (await portable.Audit.GetRecentRoundsAsync()).Count);
        }
        finally { await existing.StopAsync(); }
        await portable.StopAsync();
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, portablePort);
        listener.Start(); listener.Stop();
    }
    [TestMethod]
    public async Task DemoViewModel_SyncHistoryFilterAndCsv_WorkWithoutUiFramework()
    {
        Directory.CreateDirectory(_directory);
        var config = new AppConfig();
        config.Node = config.Node with { OperationParams = config.Node.OperationParams with { SampleTimeoutMs = 150 } };
        AppConfigManager.Save(config, Path.Combine(_directory, "node_config.json"));
        var dialogs = new Dialogs { SavePath = Path.Combine(_directory, "history.csv") };
        await using var vm = new ClientViewModel(_directory, dialogs);
        await vm.InitializeAsync();
        Assert.IsTrue(vm.IsRunning); Assert.IsTrue(vm.IsMaster);
        await vm.SyncCommand.ExecuteAsync(null); vm.Tick();
        Assert.IsTrue(vm.Samples.Count > 0); Assert.AreEqual(1, vm.History.Count);
        Assert.IsTrue(vm.Nodes.Count == 6); Assert.AreNotEqual("--", vm.After);
        vm.LogFilter = "Step"; Assert.IsTrue(vm.Logs.All(x => x.Category == "Step"));
        await vm.ExportHistoryCommand.ExecuteAsync(null);
        Assert.AreEqual(2, File.ReadAllLines(dialogs.SavePath).Length);
        dialogs.SavePath = Path.Combine(_directory, "audit.csv");
        await vm.ExportAuditCommand.ExecuteAsync(null);
        Assert.IsTrue(File.ReadAllLines(dialogs.SavePath).Length > 1);
        vm.ConfigPort = "bad-port";
        await vm.SaveConfigCommand.ExecuteAsync(null);
        StringAssert.Contains(vm.Status, "số nguyên"); Assert.IsTrue(vm.IsRunning);
        Assert.AreEqual(config.Node.ListenPort, AppConfigManager.Load(Path.Combine(_directory, "node_config.json")).Node.ListenPort);
        await vm.StopCommand.ExecuteAsync(null); Assert.IsFalse(vm.IsRunning);
        await vm.StartCommand.ExecuteAsync(null); Assert.IsTrue(vm.IsRunning);
    }
    [TestMethod]
    public async Task StopDuringDemoRound_CancelsAndCanRestart()
    {
        var config = new AppConfig();
        await using var session = new ClientSession(config, _directory);
        await session.StartAsync();
        RoundSummary? summary = null; session.RoundCompleted += value => summary = value;
        var sync = session.SyncAsync();
        await session.StopAsync(); await sync;
        Assert.IsFalse(session.IsRunning);
        Assert.AreEqual(RoundOutcome.Cancelled, summary?.Outcome);
        await session.StartAsync(); Assert.IsTrue(session.IsRunning);
    }
    [TestMethod]
    public void CorruptConfiguration_IsPreserved()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "node_config.json"); File.WriteAllText(path, "{bad");
        Assert.ThrowsException<System.Text.Json.JsonException>(() => new ClientViewModel(_directory, new Dialogs()));
        Assert.AreEqual("{bad", File.ReadAllText(path));
    }
    [TestMethod]
    public async Task ClosingViewModelDuringSync_WaitsAndFlushesHistory()
    {
        var vm = new ClientViewModel(_directory, new Dialogs());
        await vm.InitializeAsync();
        var sync = vm.SyncCommand.ExecuteAsync(null);
        await vm.DisposeAsync(); await sync;
        Assert.IsFalse(vm.IsRunning);
        Assert.IsFalse(vm.StartCommand.CanExecute(null));
        await using var audit = new Berkeley.Desktop.Persistence.SqliteAuditSink(Path.Combine(_directory, "demo.db"));
        Assert.AreEqual("Cancelled", (await audit.GetRecentRoundsAsync()).Single().Outcome);
    }
    private sealed class Dialogs : IClientDialogs
    {
        public string? SavePath;
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
        public Task<string?> SaveCsvAsync(string name) => Task.FromResult(SavePath);
    }
}
