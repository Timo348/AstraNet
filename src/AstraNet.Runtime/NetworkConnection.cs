using AstraNet.Transport;

namespace AstraNet.Runtime;

/// <summary>A server-side peer. Its ID remains stable for the lifetime of the connection.</summary>
public sealed class NetworkConnection
{
    private int _ready;
    internal NetworkConnection(TcpFrameConnection transport) => Transport = transport;
    internal TcpFrameConnection Transport { get; }
    internal bool IsReady => Volatile.Read(ref _ready) != 0;
    internal void MarkReady() => Volatile.Write(ref _ready, 1);
    public uint Id => Transport.Id;
    public bool IsConnected => IsReady && !Transport.IsClosed;
    public void Disconnect() => Transport.Close();
}
