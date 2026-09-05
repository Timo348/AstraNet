using System.Collections.Concurrent;
using System.Net.Sockets;
using AstraNet.Core;
using AstraNet.Transport;

namespace AstraNet.Runtime;

/// <summary>TCP client. Register messages and behaviours before connecting.</summary>
public sealed class NetworkClient : INetworkContext, IAsyncDisposable
{
    private readonly ConcurrentDictionary<uint, Action<NetworkReader>> _handlers = new();
    private readonly ConcurrentDictionary<(uint, ushort), NetworkBehaviourBase> _behaviours = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly AsyncLocal<CallbackScope?> _callbackScope = new();
    private readonly object _shutdownLock = new();
    private TcpFrameConnection? _transport;
    private Task? _receiveTask;
    private int _disposed;
    private Task? _disconnectTask;
    private Task? _disposeTask;
    private CancellationTokenSource? _connecting;

    public bool IsServer => false;
    public bool IsConnected => _transport is { IsClosed: false } && ConnectionId != 0;
    public uint ConnectionId { get; private set; }
    public Exception? LastError { get; private set; }
    public event Action<Exception>? Error;
    public event Action? Disconnected;

    public void OnMessage<T>(uint messageId, Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!_handlers.TryAdd(messageId, reader =>
        {
            T value = NetworkSerializer<T>.Read(reader);
            reader.EnsureEnd();
            handler(value);
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

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_transport is not null) throw new InvalidOperationException("Disconnect the previous connection before reconnecting.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            lock (_shutdownLock)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                _disconnectTask = null;
                _connecting = deadline;
            }
            var socket = new TcpClient();
            TcpFrameConnection? transport = null;
            try
            {
                await socket.ConnectAsync(host, port, deadline.Token).ConfigureAwait(false);
                transport = new TcpFrameConnection(socket);
                byte[]? hello = await transport.ReadFrameAsync(deadline.Token).ConfigureAwait(false);
                if (hello is null) throw new NetworkProtocolException("Server disconnected before the handshake.");
                var reader = new NetworkReader(hello);
                if (reader.ReadByte() != (byte)PacketKind.Hello)
                    throw new NetworkProtocolException("Server did not send a Hello handshake.");
                uint connectionId = reader.ReadUInt32();
                reader.EnsureEnd();
                if (connectionId == 0) throw new NetworkProtocolException("Server assigned an invalid connection ID.");
                ConnectionId = connectionId;
                _transport = transport;
                _receiveTask = ReceiveLoopAsync(transport);
            }
            catch (Exception error)
            {
                if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
                else socket.Dispose();
                ReportError(error);
                throw;
            }
            finally
            {
                lock (_shutdownLock) _connecting = null;
            }
        }
        finally { _lifecycle.Release(); }
    }

    public Task SendAsync<T>(uint messageId, T message, CancellationToken cancellationToken = default)
        => SendFrameAsync(Protocol.Message(messageId, message), cancellationToken);

    void INetworkContext.SendRpc(NetworkBehaviourBase behaviour, uint rpcId, bool serverRpc, byte[] payload)
    {
        if (!serverRpc) throw new InvalidOperationException("A client cannot send a ClientRpc.");
        if (!_behaviours.TryGetValue((behaviour.ObjectId, behaviour.BehaviourId), out var registered) ||
            !ReferenceEquals(registered, behaviour))
            throw new InvalidOperationException("RPC sender is not registered with this client.");
        SendFrameAsync(Protocol.Rpc(behaviour, rpcId, serverRpc, payload), CancellationToken.None)
            .ConfigureAwait(false).GetAwaiter().GetResult();
    }

    private async Task SendFrameAsync(byte[] frame, CancellationToken token)
    {
        var transport = _transport;
        if (transport is null || !IsConnected) throw new InvalidOperationException("Client is not connected.");
        try { await transport.WriteFrameAsync(frame, token).ConfigureAwait(false); }
        catch (Exception error)
        {
            if (error is not OperationCanceledException || !token.IsCancellationRequested) ReportError(error);
            throw;
        }
    }

    private async Task ReceiveLoopAsync(TcpFrameConnection transport)
    {
        try
        {
            while (!transport.IsClosed)
            {
                byte[]? frame = await transport.ReadFrameAsync().ConfigureAwait(false);
                if (frame is null) break;
                CallbackScope.Run(_callbackScope, () => Dispatch(frame));
            }
        }
        catch (OperationCanceledException) when (transport.IsClosed) { }
        catch (ObjectDisposedException) when (transport.IsClosed) { }
        catch (Exception error) { ReportError(error); }
        finally
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            try { CallbackScope.Run(_callbackScope, () => Disconnected?.Invoke()); }
            catch (Exception error) { ReportError(error); }
        }
    }

    private void Dispatch(byte[] frame)
    {
        var reader = new NetworkReader(frame);
        switch ((PacketKind)reader.ReadByte())
        {
            case PacketKind.UserMessage:
                uint messageId = reader.ReadUInt32();
                if (!_handlers.TryGetValue(messageId, out var handler))
                    throw new NetworkProtocolException($"Unknown message ID {messageId}.");
                handler(reader);
                break;
            case PacketKind.ClientRpc:
                var rpcBehaviour = ReadBehaviour(reader);
                uint rpcId = reader.ReadUInt32();
                lock (rpcBehaviour)
                {
                    if (!rpcBehaviour.__AstraNet_InvokeClientRpc(rpcId, reader))
                        throw new NetworkProtocolException($"Unknown ClientRpc ID {rpcId}.");
                    reader.EnsureEnd();
                }
                break;
            case PacketKind.State:
                var stateBehaviour = ReadBehaviour(reader);
                lock (stateBehaviour)
                {
                    stateBehaviour.__AstraNet_ReadState(reader);
                    reader.EnsureEnd();
                }
                break;
            default:
                throw new NetworkProtocolException("Server sent a packet kind that is not permitted on a client.");
        }
    }

    private NetworkBehaviourBase ReadBehaviour(NetworkReader reader)
    {
        uint objectId = reader.ReadUInt32();
        ushort behaviourId = reader.ReadUInt16();
        if (!_behaviours.TryGetValue((objectId, behaviourId), out var behaviour))
            throw new NetworkProtocolException($"Unknown behaviour {objectId}/{behaviourId}.");
        return behaviour;
    }

    private void ReportError(Exception error)
    {
        LastError = error;
        if (Error is null) return;
        foreach (Action<Exception> handler in Error.GetInvocationList())
        {
            try { CallbackScope.Run(_callbackScope, () => handler(error)); }
            catch (Exception subscriberError) { LastError = new AggregateException(error, subscriberError); }
        }
    }

    public Task DisconnectAsync()
    {
        Task shutdown;
        lock (_shutdownLock)
        {
            _connecting?.Cancel();
            _transport?.Close();
            _disconnectTask ??= Task.Run(DisconnectCoreAsync);
            shutdown = _disconnectTask;
        }
        return _callbackScope.Value is { Active: true } ? Task.CompletedTask : shutdown;
    }

    private async Task DisconnectCoreAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _transport?.Close();
            if (_receiveTask is not null) await _receiveTask.ConfigureAwait(false);
            _receiveTask = null;
            _transport = null;
            ConnectionId = 0;
        }
        finally { _lifecycle.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        Task shutdown;
        lock (_shutdownLock)
        {
            Interlocked.Exchange(ref _disposed, 1);
            _connecting?.Cancel();
            _transport?.Close();
            _disconnectTask ??= Task.Run(DisconnectCoreAsync);
            _disposeTask ??= FinishDisposeAsync(_disconnectTask);
            shutdown = _disposeTask;
        }
        return _callbackScope.Value is { Active: true } ? ValueTask.CompletedTask : new ValueTask(shutdown);
    }

    private async Task FinishDisposeAsync(Task disconnected)
    {
        await disconnected.ConfigureAwait(false);
        foreach (var behaviour in _behaviours.Values) behaviour.Detach();
        _behaviours.Clear();
    }
}
