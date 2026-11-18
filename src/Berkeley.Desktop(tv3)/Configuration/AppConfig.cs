using System.Net;
using System.Net.Sockets;
using Berkeley.Contracts;

namespace Berkeley.Desktop.Configuration;

/// <summary>
/// Cấu hình toàn diện cho ứng dụng Desktop Node.
/// Hỗ trợ chạy thực tế giữa 6 máy laptop trong mạng LAN/Wi-Fi hoặc giả lập trên 1 máy.
/// </summary>
public sealed class AppConfig
{
    public NodeConfig Node { get; set; } = CreateDefaultNodeConfig("Node1", 5001);
    public bool AutoStartOnLaunch { get; set; } = true;
    public bool UseSimulatedCluster { get; set; } = true;
    public bool AutoSyncEnabled { get; set; } = false;
    public int AutoSyncIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Ngưỡng sai số tối đa chấp nhận được để coi là đồng bộ thành công (tính bằng giây).
    /// Mặc định 60.0 giây theo yêu cầu nghiệm thu của đồ án, có thể cấu hình nhỏ hơn (ví dụ 1.0s hoặc 0.1s).
    /// </summary>
    public double AcceptanceThresholdSeconds { get; set; } = 60.0;

    public static NodeConfig CreateDefaultNodeConfig(string nodeId, int port)
    {
        var allPresetNodes = new List<(string Id, int Port)>
        {
            ("Node1", 5001),
            ("Node2", 5002),
            ("Node3", 5003),
            ("Node4", 5004),
            ("Node5", 5005),
            ("Node6", 5006),
        };

        var peers = allPresetNodes
            .Where(p => p.Id != nodeId)
            .Select(p => new PeerConfig(p.Id, "127.0.0.1", p.Port))
            .ToList();

        return new NodeConfig(
            NodeId: nodeId,
            DisplayName: $"Trạm {nodeId}",
            ListenAddress: "0.0.0.0",
            ListenPort: port,
            Peers: peers,
            OperationParams: new NodeOperationParams(
                HeartbeatIntervalMs: 2000,
                HeartbeatTimeoutMs: 6000,
                ElectionTimeoutMs: 5000,
                SampleTimeoutMs: 3000,
                SampleMaxAgeMs: 10000,
                RoundTimeoutMs: 30000
            )
        );
    }

    /// <summary>
    /// Tạo cấu hình mẫu cho mạng 6 máy laptop Wi-Fi/LAN thật.
    /// Dải IP mẫu 192.168.1.101 -> 192.168.1.106 cổng 5000.
    /// </summary>
    public static NodeConfig Create6LaptopNetworkConfig(string myNodeId, string myLocalIp)
    {
        var sampleNodes = new List<(string Id, string Ip, int Port)>
        {
            ("Node1", "192.168.1.101", 5000),
            ("Node2", "192.168.1.102", 5000),
            ("Node3", "192.168.1.103", 5000),
            ("Node4", "192.168.1.104", 5000),
            ("Node5", "192.168.1.105", 5000),
            ("Node6", "192.168.1.106", 5000),
        };

        var targetNode = sampleNodes.FirstOrDefault(n => n.Id == myNodeId);
        int myPort = targetNode != default ? targetNode.Port : 5000;
        string listenIp = string.IsNullOrWhiteSpace(myLocalIp) ? "0.0.0.0" : myLocalIp;

        var peers = sampleNodes
            .Where(p => p.Id != myNodeId)
            .Select(p => new PeerConfig(p.Id, p.Ip, p.Port))
            .ToList();

        return new NodeConfig(
            NodeId: myNodeId,
            DisplayName: $"Laptop {myNodeId}",
            ListenAddress: listenIp,
            ListenPort: myPort,
            Peers: peers,
            OperationParams: new NodeOperationParams(
                HeartbeatIntervalMs: 2000,
                HeartbeatTimeoutMs: 6000,
                ElectionTimeoutMs: 5000,
                SampleTimeoutMs: 3000,
                SampleMaxAgeMs: 10000,
                RoundTimeoutMs: 30000
            )
        );
    }

    /// <summary>
    /// Lấy danh sách các địa chỉ IP IPv4 đang hoạt động trên máy tính (Wi-Fi, Ethernet).
    /// </summary>
    public static List<string> GetLocalIPv4Addresses()
    {
        var list = new List<string> { "0.0.0.0 (Mọi card mạng)", "127.0.0.1 (Localhost)" };
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                {
                    list.Add(ip.ToString());
                }
            }
        }
        catch
        {
            // Bỏ qua lỗi DNS
        }
        return list;
    }
}
