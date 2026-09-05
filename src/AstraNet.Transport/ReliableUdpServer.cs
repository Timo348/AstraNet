using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using AstraNet.Core;

namespace AstraNet.Transport;

/// <summary>UDP endpoint that performs connection handshakes and routes datagrams to reliable sessions.</summary>
public sealed class ReliableUdpServer : IAsyncDisposable
{
    private readonly int maxConnections;
    private readonly TimeSpan idleTimeout;
    private readonly ConcurrentDictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private readonly Channel<ReliableUdpConnection> accepted = Channel.CreateBounded<ReliableUdpConnection>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly CancellationTokenSource lifetime = new();
    private readonly object lifecycle = new();
    private Task? receiveLoop;
    private Task? reaperLoop;
    private UdpClient? socket;
    private uint nextConnectionId;
    private int disposed;

    public ReliableUdpServer(int maxConnections = 128, TimeSpan? idleTimeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConnections);
        this.maxConnections = maxConnections;
        this.idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(30);
        if (this.idleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
    }

    public int Port { get; private set; }
    public int ConnectionCount => sessions.Count(pair => !pair.Value.Connection.IsClosed);
    public IReadOnlyCollection<ReliableUdpConnection> Connections => sessions.Values
        .Where(session => !session.Connection.IsClosed).Select(session => session.Connection).ToArray();
    public event Action<ReliableUdpConnection>? ClientConnected;
    public event Action<ReliableUdpConnection>? ClientDisconnected;
    public event Action<Exception>? Error;

    public Task StartAsync(IPAddress address, int port, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(address);
        lock (lifecycle)
        {
            if (socket is not null) throw new InvalidOperationException("The UDP server has already been started.");
            socket = new UdpClient(new IPEndPoint(address, port));
            Port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
            receiveLoop = ReceiveAsync();
            reaperLoop = ReapAsync();
        }
        return Task.CompletedTask;
    }

    public async Task<ReliableUdpConnection?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        try { return await accepted.Reader.ReadAsync(cancellationToken).ConfigureAwait(false); }
        catch (ChannelClosedException) { return null; }
    }

    private async Task ReceiveAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var result = await socket!.ReceiveAsync(lifetime.Token).ConfigureAwait(false);
                await RouteAsync(result.RemoteEndPoint, result.Buffer).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { Report(error); }
    }

    private async Task RouteAsync(IPEndPoint endpoint, byte[] bytes)
    {
        string key = EndpointKey(endpoint);
        if (!UdpProtocol.TryDecode(bytes, out var datagram, out var decodeError))
        {
            Report(new NetworkProtocolException(decodeError!));
            return;
        }
        if (datagram.IsHandshake)
        {
            if (datagram.IsHandshakeResponse || datagram.ConnectionId != 0)
            {
                Report(new NetworkProtocolException("Invalid UDP handshake request."));
                return;
            }
            if (sessions.TryGetValue(key, out var existing))
            {
                await SendHandshakeAsync(endpoint, existing.Connection.Id).ConfigureAwait(false);
                existing.LastSeen = DateTime.UtcNow;
                return;
            }
            if (sessions.Count >= maxConnections) return;
            uint id = NextId();
            var peer = new ReliableUdpPeer(id, endpoint, (packet, token) => SendDatagramAsync(endpoint, packet, token));
            var connection = new ReliableUdpConnection(peer);
            var newSession = new Session(endpoint, connection);
            if (!sessions.TryAdd(key, newSession))
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                return;
            }
            await SendHandshakeAsync(endpoint, id).ConfigureAwait(false);
            if (!accepted.Writer.TryWrite(connection))
            {
                sessions.TryRemove(key, out _);
                await connection.DisposeAsync().ConfigureAwait(false);
                return;
            }
            try { ClientConnected?.Invoke(connection); }
            catch (Exception error) { Report(error); }
            return;
        }
        if (!sessions.TryGetValue(key, out var session)) return;
        if (datagram.ConnectionId != session.Connection.Id)
        {
            Report(new NetworkProtocolException("UDP datagram connection ID does not match its endpoint."));
            RemoveSession(key, session);
            return;
        }
        session.LastSeen = DateTime.UtcNow;
        if (datagram.IsDisconnect)
        {
            RemoveSession(key, session);
            return;
        }
        try
        {
            await session.Connection.ProcessDatagramAsync(bytes).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Report(error);
            RemoveSession(key, session);
        }
    }

    private async Task SendHandshakeAsync(IPEndPoint endpoint, uint id)
    {
        var packet = UdpProtocol.Encode(UdpProtocol.HandshakeFlag | UdpProtocol.HandshakeResponseFlag,
            id, 0, 0, 0, 0, []);
        await SendDatagramAsync(endpoint, packet, lifetime.Token).ConfigureAwait(false);
    }

    private async ValueTask SendDatagramAsync(IPEndPoint endpoint, ReadOnlyMemory<byte> packet, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var current = socket ?? throw new ObjectDisposedException(nameof(ReliableUdpServer));
        await current.SendAsync(packet, endpoint, token).ConfigureAwait(false);
    }

    private async Task ReapAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                foreach (var pair in sessions.ToArray())
                {
                    if (DateTime.UtcNow - pair.Value.LastSeen <= idleTimeout) continue;
                    if (sessions.TryRemove(pair.Key, out var removed))
                    {
                        removed.Connection.Close();
                        try { ClientDisconnected?.Invoke(removed.Connection); }
                        catch (Exception error) { Report(error); }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private void RemoveSession(string key, Session session)
    {
        if (!sessions.TryRemove(new KeyValuePair<string, Session>(key, session))) return;
        session.Connection.Close();
        try { ClientDisconnected?.Invoke(session.Connection); }
        catch (Exception error) { Report(error); }
    }

    private uint NextId()
    {
        uint id = unchecked(++nextConnectionId);
        if (id == 0) id = unchecked(++nextConnectionId);
        while (sessions.Values.Any(session => session.Connection.Id == id)) id = unchecked(++nextConnectionId);
        return id;
    }

    private static string EndpointKey(IPEndPoint endpoint) => $"{endpoint.Address}|{endpoint.Port}";

    private void Report(Exception error)
    {
        if (Error is null) return;
        foreach (Action<Exception> handler in Error.GetInvocationList())
        {
            try { handler(error); } catch { /* telemetry must not stop packet routing */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        socket?.Dispose();
        accepted.Writer.TryComplete();
        foreach (var pair in sessions.ToArray())
        {
            if (sessions.TryRemove(pair.Key, out var session)) await session.Connection.DisposeAsync().ConfigureAwait(false);
        }
        if (receiveLoop is not null) await receiveLoop.ConfigureAwait(false);
        if (reaperLoop is not null) await reaperLoop.ConfigureAwait(false);
        lifetime.Dispose();
    }

    private sealed class Session(IPEndPoint endpoint, ReliableUdpConnection connection)
    {
        public IPEndPoint Endpoint { get; } = endpoint;
        public ReliableUdpConnection Connection { get; } = connection;
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    }
}
