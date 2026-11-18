using CommunityToolkit.Mvvm.ComponentModel;

namespace Berkeley.Desktop.ViewModels;

public partial class ConfigPeerItem : ObservableObject
{
    [ObservableProperty]
    private string _nodeId = string.Empty;

    [ObservableProperty]
    private string _address = string.Empty;

    [ObservableProperty]
    private int _port = 5000;

    public ConfigPeerItem() { }

    public ConfigPeerItem(string nodeId, string address, int port)
    {
        NodeId = nodeId;
        Address = address;
        Port = port;
    }
}
