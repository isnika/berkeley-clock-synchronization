using System.Net;
namespace Berkeley.Desktop.Configuration;
public static class ConfigValidator
{
    public static void Validate(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.AutoSyncIntervalSeconds is < 1 or > 86400) throw new ArgumentException("Chu kỳ AutoSync phải từ 1 đến 86400 giây.");
        var node = config.Node ?? throw new ArgumentException("Thiếu Node.");
        if (string.IsNullOrWhiteSpace(node.NodeId) || node.NodeId != node.NodeId.Trim()) throw new ArgumentException("Node ID không hợp lệ.");
        if (!IPAddress.TryParse(node.ListenAddress, out _) || node.ListenPort is < 1 or > 65535) throw new ArgumentException("IP/cổng lắng nghe không hợp lệ.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { node.NodeId };
        foreach (var peer in node.Peers ?? throw new ArgumentException("Thiếu peers."))
        {
            if (peer == null || string.IsNullOrWhiteSpace(peer.NodeId) || peer.NodeId != peer.NodeId.Trim() || !ids.Add(peer.NodeId)) throw new ArgumentException("Node ID peer trống hoặc trùng.");
            if (!IPAddress.TryParse(peer.Address, out _) || peer.Port is < 1 or > 65535) throw new ArgumentException($"IP/cổng của {peer.NodeId} không hợp lệ.");
        }
        var p = node.OperationParams ?? throw new ArgumentException("Thiếu tham số vận hành.");
        if (p.HeartbeatIntervalMs <= 0 || p.HeartbeatTimeoutMs <= p.HeartbeatIntervalMs || p.ElectionTimeoutMs <= 0 || p.SampleTimeoutMs <= 0 || p.SampleTimeoutMs > int.MaxValue / 2 || p.SampleMaxAgeMs < p.SampleTimeoutMs || p.RoundTimeoutMs <= p.SampleTimeoutMs) throw new ArgumentException("Các timeout không hợp lệ.");
        if (!double.IsFinite(config.AcceptanceThresholdSeconds) || config.AcceptanceThresholdSeconds <= 0) throw new ArgumentException("Ngưỡng sai số phải hữu hạn và lớn hơn 0.");
    }
}
