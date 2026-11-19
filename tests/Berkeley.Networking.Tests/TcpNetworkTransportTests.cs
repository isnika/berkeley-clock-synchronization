using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Berkeley.Contracts;
using Berkeley.Networking;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Berkeley.Networking.Tests;

[TestClass]
public sealed class TcpNetworkTransportTests
{
    [TestMethod]
    public async Task Transport_ReadsFragmentedLengthAndJsonFrame()
    {
        var config = NetworkingTestHelpers.Config(
            "Node1",
            0,
            [("Node2", 5002)],
            heartbeatTimeoutMs: 500);
        var audit = new TestAuditSink();
        await using var transport = new TcpNetworkTransport(config, new TestClock(), audit, autoStart: false);

        var received = new TaskCompletionSource<MessageEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.MessageReceived += message => received.TrySetResult(message);
        await transport.StartAsync();

        var envelope = MessageEnvelope.Create(
            messageType: MessageTypes.Election,
            sourceNodeId: "Node2",
            sourceBootId: Guid.NewGuid(),
            targetNodeId: "Node1",
            payload: new ElectionPayload("Node2", 3),
            leaderSessionId: 3);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(envelope);
        byte[] header = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payload.Length));

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, transport.LocalPort);
            await using var stream = client.GetStream();

            await stream.WriteAsync(header.AsMemory(0, 1));
            await stream.WriteAsync(header.AsMemory(1));
            await stream.WriteAsync(payload.AsMemory(0, Math.Min(5, payload.Length)));
            await stream.WriteAsync(payload.AsMemory(Math.Min(5, payload.Length)));
            await stream.FlushAsync();
        }

        var actual = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(MessageTypes.Election, actual.MessageType);
        Assert.AreEqual("Node2", actual.SourceNodeId);
        Assert.AreEqual(3, actual.LeaderSessionId);
    }

    [TestMethod]
    public async Task Transport_ClosesConnectionWhenFrameReadTimesOut()
    {
        var config = NetworkingTestHelpers.Config(
            "Node1",
            0,
            [("Node2", 5002)],
            heartbeatTimeoutMs: 80);
        await using var transport = new TcpNetworkTransport(
            config,
            new TestClock(),
            new TestAuditSink(),
            autoStart: false);
        await transport.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, transport.LocalPort);
        await using var stream = client.GetStream();
        byte[] header = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(32));
        await stream.WriteAsync(header);
        await stream.WriteAsync(new byte[] { 1, 2 });
        await stream.FlushAsync();

        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var oneByte = new byte[1];
        int read;
        try
        {
            read = await stream.ReadAsync(oneByte, closeCts.Token);
        }
        catch (IOException)
        {
            read = 0;
        }

        Assert.AreEqual(0, read);
    }

    [TestMethod]
    public async Task Transport_StopThenStart_ReleasesAndRebindsListener()
    {
        var config = NetworkingTestHelpers.Config("Node1", 0, []);
        await using var transport = new TcpNetworkTransport(
            config,
            new TestClock(),
            new TestAuditSink(),
            autoStart: false);

        await transport.StartAsync();
        int firstPort = transport.LocalPort;
        Assert.IsTrue(await NetworkingTestHelpers.CanConnectAsync(firstPort));

        await transport.StopAsync();
        Assert.IsFalse(transport.IsStarted);
        Assert.IsFalse(await NetworkingTestHelpers.CanConnectAsync(firstPort));

        await transport.StartAsync();
        Assert.IsTrue(transport.IsStarted);
        Assert.IsTrue(await NetworkingTestHelpers.CanConnectAsync(transport.LocalPort));
    }
}
