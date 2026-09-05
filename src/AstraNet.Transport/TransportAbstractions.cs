namespace AstraNet.Transport;

/// <summary>Available delivery semantics for transports that support datagrams.</summary>
public enum DeliveryMode : byte
{
    Unreliable = 0,
    ReliableOrdered = 1
}

/// <summary>Concrete wire connection used by the high-level runtime.</summary>
public interface INetworkFrameConnection : IAsyncDisposable
{
    uint Id { get; }
    bool IsClosed { get; }
    Task<byte[]?> ReadFrameAsync(CancellationToken cancellationToken = default);
    Task WriteFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
    Task SendAsync(ReadOnlyMemory<byte> payload, DeliveryMode mode, CancellationToken cancellationToken = default);
    void Close();
}

/// <summary>Wraps transport selection without exposing socket-specific details to gameplay code.</summary>
public enum NetworkTransportKind : byte
{
    Tcp = 0,
    ReliableUdp = 1
}
