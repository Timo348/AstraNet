using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AstraNet.Core;
using AstraNet.Transport;

namespace AstraNet.Runtime;

/// <summary>Multi-client TCP host with explicit message IDs and explicit state replication.</summary>
public sealed class NetworkServer : INetworkContext, IAsyncDisposable
{
    private readonly ConcurrentDictionary<uint, NetworkConnection> _connections = new();
    private readonly ConcurrentDictionary<uint, Task> _peerTasks = new();
    private readonly ConcurrentDictionary<uint, Action<NetworkConnection, NetworkReader>> _handlers = new();
    private readonly ConcurrentDictionary<(uint, ushort), NetworkBehaviourBase> _behaviours = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AsyncLocal<CallbackScope?> _callbackScope = new();
    private readonly object _disposeLock = new();
    private readonly int _maxConnections;
    private readonly NetworkTransportKind _transportKind;
    private TcpListener? _listener;
    private ReliableUdpServer? _udpServer;
    private Task? _acceptTask;
    private int _nextConnectionId;
    private int _disposed;
    private Task? _disposeTask;

    public NetworkServer(int maxConnections = 128) : this(NetworkTransportKind.Tcp, maxConnections) { }

    public NetworkServer(NetworkTransportKind transportKind, int maxConnections = 128)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConnections);
        if (!Enum.IsDefined(transportKind)) throw new ArgumentOutOfRangeException(nameof(transportKind));
        _transportKind = transportKind;
        _maxConnections = maxConnections;
    }

    public bool IsServer => true;
    public int Port { get; private set; }
    public int ConnectionCount => _connections.Values.Count(connection => connection.IsConnected);
    public IReadOnlyCollection<NetworkConnection> Connections => _connections.Values.Where(connection => connection.IsConnected).ToArray();
    public Exception? LastError { get; private set; }
    public event Action<NetworkConnection>? ClientConnected;
    public event Action<NetworkConnection>? ClientDisconnected;
    public event Action<NetworkConnection?, Exception>? Error;

    public async Task StartAsync(IPAddress address, int port, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(address);
        if (_listener is not null || _udpServer is not null) throw new InvalidOperationException("The server has already been started.");
        if (_transportKind == NetworkTransportKind.ReliableUdp)
        {
            _udpServer = new ReliableUdpServer(_maxConnections);
            _udpServer.Error += error => ReportError(null, error);
            await _udpServer.StartAsync(address, port, cancellationToken).ConfigureAwait(false);
            Port = _udpServer.Port;
            _acceptTask = AcceptUdpLoopAsync();
            return;
        }
        _listener = new TcpListener(address, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = AcceptLoopAsync();
    }

    public void OnMessage<T>(uint messageId, Action<NetworkConnection, T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!_handlers.TryAdd(messageId, (connection, reader) =>
        {
            T value = NetworkSerializer<T>.Read(reader);
            reader.EnsureEnd();
            handler(connection, value);
        })) throw new InvalidOperationException($"Message ID {messageId} already has a handler.");
    }

    public void RegisterBehaviour(uint objectId, ushort behaviourId, NetworkBehaviourBase behaviour)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(behaviour);
        if (!_behaviours.TryAdd((objectId, behaviourId), behaviour))
            throw new InvalidOperationException($"Behaviour {objectId}/{behaviourId} is already registered.");
        try { behaviour.Attach(this, objectId, behaviourId); }
        catch { _behaviours.TryRemove((objectId, behaviourId), out _); throw; }
    }

    public Task SendAsync<T>(uint connectionId, uint messageId, T message, CancellationToken cancellationToken = default)
        => SendAsync(connectionId, messageId, message, DeliveryMode.ReliableOrdered, cancellationToken);

    public Task SendAsync<T>(uint connectionId, uint messageId, T message, DeliveryMode mode,
        CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(connectionId, out var connection) || !connection.IsConnected)
            throw new KeyNotFoundException($"Connection {connectionId} is not connected.");
        return SendFrameAsync(connection, Protocol.Message(messageId, message), mode, cancellationToken);
    }

    public Task BroadcastAsync<T>(uint messageId, T message, CancellationToken cancellationToken = default)
        => BroadcastAsync(messageId, message, DeliveryMode.ReliableOrdered, cancellationToken);

    public Task BroadcastAsync<T>(uint messageId, T message, DeliveryMode mode,
        CancellationToken cancellationToken = default)
        => BroadcastFrameAsync(Protocol.Message(messageId, message), mode, cancellationToken);

    /// <summary>Sends a full snapshot for each registered behaviour on the specified object.</summary>
    public async Task ReplicateAsync(uint objectId, CancellationToken cancellationToken = default)
        => await ReplicateAsync(objectId, DeliveryMode.ReliableOrdered, cancellationToken).ConfigureAwait(false);

    public async Task ReplicateAsync(uint objectId, DeliveryMode mode, CancellationToken cancellationToken = default)
    {
        var behaviours = _behaviours.Where(pair => pair.Key.Item1 == objectId)
            .OrderBy(pair => pair.Key.Item2).Select(pair => pair.Value).ToArray();
        if (behaviours.Length == 0) throw new KeyNotFoundException($"Object {objectId} is not registered.");
        foreach (var behaviour in behaviours)
        {
            var writer = new NetworkWriter();
            writer.WriteByte((byte)PacketKind.State);
            writer.WriteUInt32(behaviour.ObjectId);
            writer.WriteUInt16(behaviour.BehaviourId);
            lock (behaviour) behaviour.__AstraNet_WriteState(writer);
            await BroadcastFrameAsync(writer.ToArray(), mode, cancellationToken).ConfigureAwait(false);
        }
    }

    void INetworkContext.SendRpc(NetworkBehaviourBase behaviour, uint rpcId, bool serverRpc, byte[] payload)
    {
        if (serverRpc) throw new InvalidOperationException("A server cannot send a ServerRpc to a client.");
        VerifyRegistered(behaviour);
        BroadcastFrameAsync(Protocol.Rpc(behaviour, rpcId, serverRpc, payload), DeliveryMode.ReliableOrdered, _lifetime.Token)
            .ConfigureAwait(false).GetAwaiter().GetResult();
    }

    private void VerifyRegistered(NetworkBehaviourBase behaviour)
    {
        if (!_behaviours.TryGetValue((behaviour.ObjectId, behaviour.BehaviourId), out var registered) ||
            !ReferenceEquals(registered, behaviour))
            throw new InvalidOperationException("RPC sender is not registered with this server.");
    }

    private async Task BroadcastFrameAsync(byte[] frame, DeliveryMode mode, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var tasks = _connections.Values.Where(connection => connection.IsConnected)
            .Select(connection => SendFrameAsync(connection, frame, mode, token)).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task SendFrameAsync(NetworkConnection connection, byte[] frame, DeliveryMode mode, CancellationToken token)
    {
        try { await connection.Transport.SendAsync(frame, mode, token).ConfigureAwait(false); }
        catch (Exception error)
        {
            if (error is not OperationCanceledException || !token.IsCancellationRequested) ReportError(connection, error);
            throw;
        }
    }

    private async Task AcceptUdpLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var transport = await _udpServer!.AcceptAsync(_lifetime.Token).ConfigureAwait(false);
                if (transport is null) break;
                var connection = new NetworkConnection(transport);
                if (!_connections.TryAdd(connection.Id, connection))
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                    ReportError(null, new IOException("Connection ID space exhausted."));
                    continue;
                }
                Task task = RunPeerAsync(connection);
                _peerTasks[connection.Id] = task;
                _ = task.ContinueWith(completed =>
                {
                    _peerTasks.TryRemove(connection.Id, out _);
                    if (completed.Exception is not null) ReportError(connection, completed.Exception);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { ReportError(null, error); }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                TcpClient socket = await _listener!.AcceptTcpClientAsync(_lifetime.Token).ConfigureAwait(false);
                if (_connections.Count >= _maxConnections)
                {
                    socket.Dispose();
                    ReportError(null, new IOException($"The server connection limit ({_maxConnections}) was reached."));
                    continue;
                }
                uint id = unchecked((uint)Interlocked.Increment(ref _nextConnectionId));
                if (id == 0) id = unchecked((uint)Interlocked.Increment(ref _nextConnectionId));
                var connection = new NetworkConnection(new TcpFrameConnection(socket, id));
                if (!_connections.TryAdd(id, connection))
                {
                    await connection.Transport.DisposeAsync().ConfigureAwait(false);
                    ReportError(null, new IOException("Connection ID space exhausted."));
                    continue;
                }
                Task task = RunPeerAsync(connection);
                _peerTasks[id] = task;
                _ = task.ContinueWith(completed =>
                {
                    _peerTasks.TryRemove(id, out _);
                    if (completed.Exception is not null) ReportError(connection, completed.Exception);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (SocketException) when (_lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { ReportError(null, error); }
    }

    private async Task RunPeerAsync(NetworkConnection connection)
    {
        try
        {
            var hello = new NetworkWriter();
            hello.WriteByte((byte)PacketKind.Hello);
            hello.WriteUInt32(connection.Id);
            await connection.Transport.WriteFrameAsync(hello.ToArray(), _lifetime.Token).ConfigureAwait(false);
            connection.MarkReady();
            CallbackScope.Run(_callbackScope, () => ClientConnected?.Invoke(connection));
            while (!_lifetime.IsCancellationRequested && connection.IsConnected)
            {
                var frame = await connection.Transport.ReadFrameAsync(_lifetime.Token).ConfigureAwait(false);
                if (frame is null) break;
                CallbackScope.Run(_callbackScope, () => Dispatch(connection, frame));
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || !connection.IsConnected) { }
        catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested || !connection.IsConnected) { }
        catch (Exception error) { ReportError(connection, error); }
        finally
        {
            _connections.TryRemove(connection.Id, out _);
            await connection.Transport.DisposeAsync().ConfigureAwait(false);
            try { CallbackScope.Run(_callbackScope, () => ClientDisconnected?.Invoke(connection)); }
            catch (Exception error) { ReportError(connection, error); }
        }
    }

    private void Dispatch(NetworkConnection connection, byte[] frame)
    {
        var reader = connection.Reader;
        reader.Reset(frame);
        switch ((PacketKind)reader.ReadByte())
        {
            case PacketKind.UserMessage:
                uint messageId = reader.ReadUInt32();
                if (!_handlers.TryGetValue(messageId, out var handler))
                    throw new NetworkProtocolException($"Unknown message ID {messageId}.");
                handler(connection, reader);
                break;
            case PacketKind.ServerRpc:
                uint objectId = reader.ReadUInt32();
                ushort behaviourId = reader.ReadUInt16();
                uint rpcId = reader.ReadUInt32();
                if (!_behaviours.TryGetValue((objectId, behaviourId), out var behaviour))
                    throw new NetworkProtocolException($"Unknown behaviour {objectId}/{behaviourId}.");
                lock (behaviour)
                {
                    if (!behaviour.__AstraNet_InvokeServerRpc(rpcId, reader))
                        throw new NetworkProtocolException($"Unknown ServerRpc ID {rpcId}.");
                    reader.EnsureEnd();
                }
                break;
            default:
                throw new NetworkProtocolException("Client sent a packet kind that is not permitted on a server.");
        }
    }

    private void ReportError(NetworkConnection? connection, Exception error)
    {
        LastError = error;
        if (Error is null) return;
        foreach (Action<NetworkConnection?, Exception> handler in Error.GetInvocationList())
        {
            try { CallbackScope.Run(_callbackScope, () => handler(connection, error)); }
            catch (Exception subscriberError) { LastError = new AggregateException(error, subscriberError); }
        }
    }

    public ValueTask DisposeAsync()
    {
        Task shutdown;
        lock (_disposeLock)
        {
            Interlocked.Exchange(ref _disposed, 1);
            _disposeTask ??= Task.Run(ShutdownAsync);
            shutdown = _disposeTask;
        }
        // A callback cannot await its own receive loop. It starts shutdown; an external call can await it.
        return _callbackScope.Value is { Active: true } ? ValueTask.CompletedTask : new ValueTask(shutdown);
    }

    private async Task ShutdownAsync()
    {
        _lifetime.Cancel();
        _listener?.Stop();
        if (_udpServer is not null) await _udpServer.DisposeAsync().ConfigureAwait(false);
        if (_acceptTask is not null) await _acceptTask.ConfigureAwait(false);
        foreach (var connection in _connections.Values) connection.Disconnect();
        await Task.WhenAll(_peerTasks.Values).ConfigureAwait(false);
        foreach (var behaviour in _behaviours.Values) behaviour.Detach();
        _behaviours.Clear();
    }
}
