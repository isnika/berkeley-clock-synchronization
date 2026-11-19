using Berkeley.Contracts;

namespace Berkeley.Networking;

/// <summary>
/// Optional networking capability used by the leadership service to observe
/// configured peers without changing the shared Contracts assembly.
/// </summary>
public interface IPeerLiveness
{
    event Action<string, PeerConnectionState>? PeerConnectionChanged;

    PeerConnectionState GetPeerConnectionState(string nodeId);
}
