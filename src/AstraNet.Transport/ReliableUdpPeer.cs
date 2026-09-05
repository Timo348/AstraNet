using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using AstraNet.Core;

namespace AstraNet.Transport;

/// <summary>
/// A small reliable-ordered UDP session. It owns no socket: the host supplies a
/// datagram sender and routes received datagrams to <see cref="ProcessDatagramAsync"/>.
/// </summary>
internal sealed class ReliableUdpPeer : INetworkFrameConnection
{
    // ACK + 32-bit ack history can represent at most 33 in-flight sequence numbers.
    // Keeping the pending window below that limit prevents a lost packet from
    // aging out of the acknowledgement bitmap before its retransmission arrives.
    internal const int MaxPendingReliable = 32;
    internal const int MaxReorderWindow = 4096;
    internal static readonly TimeSpan RetransmitAfter = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan RetryFor = TimeSpan.FromSeconds(8);

    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sendDatagram;
    private readonly Channel<byte[]> inbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4096)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private readonly Dictionary<uint, PendingReliable> pending = new();
    private readonly SortedDictionary<uint, byte[]> reordered = new();
    private readonly Task retransmitLoop;
    private UdpAckTracker received;
    private uint nextReliableSequence = 1;
    private uint nextExpectedReliableSequence = 1;
    private int disposed;
    private Exception? failure;
    private Task? disconnectTask;

    internal ReliableUdpPeer(uint id, IPEndPoint endpoint,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sendDatagram)
        : this(id, endpoint, sendDatagram, 1)
    {
    }

    internal ReliableUdpPeer(uint id, IPEndPoint endpoint,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sendDatagram,
        uint initialReliableSequence)
    {
        if (id == 0) throw new ArgumentOutOfRangeException(nameof(id));
        if (initialReliableSequence == 0) throw new ArgumentOutOfRangeException(nameof(initialReliableSequence));
        Id = id;
        RemoteEndPoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        this.sendDatagram = sendDatagram ?? throw new ArgumentNullException(nameof(sendDatagram));
        nextReliableSequence = initialReliableSequence;
        nextExpectedReliableSequence = initialReliableSequence;
        retransmitLoop = RetransmitAsync();
    }

    public uint Id { get; }
    public IPEndPoint RemoteEndPoint { get; }
    public bool IsClosed => Volatile.Read(ref disposed) != 0;

    public Task<byte[]?> ReadFrameAsync(CancellationToken cancellationToken = default)
        => ReadFrameCoreAsync(cancellationToken);

    private async Task<byte[]?> ReadFrameCoreAsync(CancellationToken cancellationToken)
    {
        try { return await inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false); }
        catch (ChannelClosedException) { return null; }
    }

    public Task WriteFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        => SendAsync(payload, DeliveryMode.ReliableOrdered, cancellationToken);

    public Task SendAsync(ReadOnlyMemory<byte> payload, DeliveryMode mode, CancellationToken cancellationToken = default)
        => SendCoreAsync(payload, mode, cancellationToken);

    private async Task SendCoreAsync(ReadOnlyMemory<byte> payload, DeliveryMode mode, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        if (payload.Length == 0 || payload.Length > UdpProtocol.MaxPayloadSize)
            throw new NetworkProtocolException($"Reliable UDP payload must be 1..{UdpProtocol.MaxPayloadSize} bytes.");
        cancellationToken.ThrowIfCancellationRequested();
        uint sequence = 0;
        TaskCompletionSource? completion = null;
        byte[] packet;
        lock (gate)
        {
            var (ack, bits) = AckSnapshot();
            if (mode == DeliveryMode.Unreliable)
            {
                packet = UdpProtocol.Encode(0, Id, 0, ack, bits, 0, payload.Span);
            }
            else if (mode == DeliveryMode.ReliableOrdered)
            {
                if (pending.Count >= MaxPendingReliable)
                    throw new NetworkBackpressureException("Reliable UDP pending window is full.");
                sequence = NextSequenceLocked();
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                packet = UdpProtocol.Encode(0, Id, sequence, ack, bits, UdpProtocol.ReliableOrderedChannel, payload.Span);
                pending.Add(sequence, new PendingReliable(payload.ToArray(), completion, DateTime.UtcNow, 0));
            }
            else throw new ArgumentOutOfRangeException(nameof(mode));
        }
        try
        {
            await sendDatagram(packet, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (sequence != 0)
            {
                lock (gate)
                {
                    if (pending.Remove(sequence, out var removed)) removed.Completion.TrySetException(error);
                }
            }
            throw;
        }
        if (completion is null) return;
        try
        {
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (gate)
            {
                if (pending.Remove(sequence, out var canceled))
                    canceled.Completion.TrySetCanceled(cancellationToken);
            }
            throw;
        }
    }

    internal async Task ProcessDatagramAsync(ReadOnlyMemory<byte> bytes)
    {
        if (!UdpProtocol.TryDecode(bytes.Span, out var datagram, out var error))
            throw new NetworkProtocolException(error!);
        if (datagram.ConnectionId != Id)
            throw new NetworkProtocolException("UDP connection ID does not match this endpoint.");
        if (datagram.IsHandshake)
            throw new NetworkProtocolException("A handshake packet was sent to an established UDP session.");
        if (datagram.IsDisconnect)
        {
            Close(new IOException("The remote UDP peer disconnected."));
            return;
        }
        List<byte[]>? deliver = null;
        bool sendAck = false;
        lock (gate)
        {
            ProcessAcks(datagram.Ack, datagram.AckBits);
            if (datagram.IsAckOnly) return;
            if (datagram.Channel == 0)
            {
                // Unreliable traffic is intentionally dropped when the application queue is full.
                inbound.Writer.TryWrite(datagram.Payload);
                sendAck = false;
            }
            else if (datagram.Channel == UdpProtocol.ReliableOrderedChannel)
            {
                if (datagram.Sequence == 0) throw new NetworkProtocolException("Reliable UDP data has no sequence.");
                sendAck = true;
                bool isNewToAckTracker = received.Mark(datagram.Sequence);
                bool isExpected = datagram.Sequence == nextExpectedReliableSequence;
                bool isAhead = UdpSequence.IsNewer(datagram.Sequence, nextExpectedReliableSequence);
                if (isExpected)
                {
                    deliver = [];
                    deliver.Add(datagram.Payload);
                    nextExpectedReliableSequence = NextSequence(nextExpectedReliableSequence);
                    while (reordered.Remove(nextExpectedReliableSequence, out var queued))
                    {
                        deliver.Add(queued);
                        nextExpectedReliableSequence = NextSequence(nextExpectedReliableSequence);
                    }
                }
                else if (isAhead)
                {
                    uint distance = datagram.Sequence - nextExpectedReliableSequence;
                    if (distance > MaxReorderWindow)
                        throw new NetworkProtocolException("Reliable UDP sequence is outside the reorder window.");
                    if (!reordered.ContainsKey(datagram.Sequence)) reordered.Add(datagram.Sequence, datagram.Payload);
                }
                else if (!isNewToAckTracker)
                {
                    // A sequence older than the delivery cursor is a duplicate.
                }
            }
            else throw new NetworkProtocolException("UDP channel is unsupported.");
        }
        if (deliver is not null)
        {
            foreach (var payload in deliver)
                if (!inbound.Writer.TryWrite(payload))
                    throw new NetworkBackpressureException("Reliable UDP receive queue is full.");
        }
        if (sendAck) await SendAckOnlyAsync().ConfigureAwait(false);
    }

    private void ProcessAcks(uint ack, uint ackBits)
    {
        if (ack == 0 && ackBits == 0) return;
        foreach (var sequence in pending.Keys.Where(sequence => IsAcked(sequence, ack, ackBits)).ToArray())
        {
            if (pending.Remove(sequence, out var item)) item.Completion.TrySetResult();
        }
    }

    private static bool IsAcked(uint sequence, uint ack, uint bits)
    {
        if (sequence == ack) return true;
        uint behind = ack - sequence;
        return behind is >= 1 and <= 32 && (bits & (1u << (int)(behind - 1))) != 0;
    }

    private (uint Ack, uint Bits) AckSnapshot() => received.HasLatest ? (received.Latest, received.Bits) : (0, 0);

    private uint NextSequenceLocked()
    {
        uint sequence = nextReliableSequence;
        nextReliableSequence = NextSequence(sequence);
        return sequence;
    }

    private static uint NextSequence(uint sequence)
    {
        sequence = unchecked(sequence + 1);
        return sequence == 0 ? 1u : sequence;
    }

    private async Task SendAckOnlyAsync()
    {
        uint ack;
        uint bits;
        lock (gate) (ack, bits) = AckSnapshot();
        if (ack == 0) return;
        var packet = UdpProtocol.Encode(UdpProtocol.AckOnlyFlag, Id, 0, ack, bits, UdpProtocol.ReliableOrderedChannel, []);
        try { await sendDatagram(packet, lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task RetransmitAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                List<(uint Sequence, PendingReliable Item, byte[] Packet)> resend = [];
                List<PendingReliable>? expired = null;
                lock (gate)
                {
                    var now = DateTime.UtcNow;
                    var (ack, bits) = AckSnapshot();
                    foreach (var pair in pending.ToArray())
                    {
                        var item = pair.Value;
                        if (now - item.FirstSent > RetryFor)
                        {
                            expired ??= [];
                            expired.Add(item);
                            pending.Remove(pair.Key);
                            continue;
                        }
                        if (now - item.LastSent < RetransmitAfter) continue;
                        if (item.Attempts >= 80) continue;
                        item.LastSent = now;
                        item.Attempts++;
                        resend.Add((pair.Key, item, UdpProtocol.Encode(0, Id, pair.Key, ack, bits,
                            UdpProtocol.ReliableOrderedChannel, item.Payload)));
                    }
                }
                if (expired is not null)
                {
                    var timeout = new TimeoutException("Reliable UDP delivery timed out.");
                    foreach (var item in expired) item.Completion.TrySetException(timeout);
                }
                foreach (var item in resend)
                {
                    try { await sendDatagram(item.Packet, lifetime.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                    catch (Exception error) { Close(error); return; }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    public void Close() => Close(new IOException("Reliable UDP connection closed."));

    private void Close(Exception reason)
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        disconnectTask = SendDisconnectAsync();
        failure = reason;
        lifetime.Cancel();
        inbound.Writer.TryComplete(reason);
        lock (gate)
        {
            foreach (var item in pending.Values) item.Completion.TrySetException(reason);
            pending.Clear();
            reordered.Clear();
        }
    }

    private async Task SendDisconnectAsync()
    {
        var packet = UdpProtocol.Encode(UdpProtocol.DisconnectFlag, Id, 0, 0, 0, 0, []);
        try { await sendDatagram(packet, CancellationToken.None).ConfigureAwait(false); }
        catch { /* disconnect is best effort */ }
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        if (disconnectTask is not null)
        {
            try { await disconnectTask.ConfigureAwait(false); }
            catch { /* disconnect is best effort */ }
        }
        try { await retransmitLoop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        lifetime.Dispose();
    }

    private sealed class PendingReliable(byte[] payload, TaskCompletionSource completion, DateTime firstSent, int attempts)
    {
        public byte[] Payload { get; } = payload;
        public TaskCompletionSource Completion { get; } = completion;
        public DateTime FirstSent { get; } = firstSent;
        public DateTime LastSent { get; set; } = firstSent;
        public int Attempts { get; set; } = attempts;
    }
}

/// <summary>Public UDP connection wrapper exposing an explicit delivery-mode send.</summary>
public sealed class ReliableUdpConnection : INetworkFrameConnection
{
    private readonly ReliableUdpPeer peer;
    private readonly Func<ValueTask>? closeTransport;
    private readonly Task? receiveLoop;
    internal ReliableUdpConnection(ReliableUdpPeer peer, Func<ValueTask>? closeTransport = null, Task? receiveLoop = null)
    {
        this.peer = peer;
        this.closeTransport = closeTransport;
        this.receiveLoop = receiveLoop;
    }
    public uint Id => peer.Id;
    public IPEndPoint RemoteEndPoint => peer.RemoteEndPoint;
    public bool IsClosed => peer.IsClosed;

    /// <summary>Performs a bounded UDP handshake and starts the datagram receive loop.</summary>
    public static async Task<ReliableUdpConnection> ConnectAsync(string host, int port,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var socket = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, deadline.Token).ConfigureAwait(false);
            var address = addresses.FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork)
                ?? throw new SocketException((int)SocketError.AddressFamilyNotSupported);
            socket.Connect(new IPEndPoint(address, port));
            var remote = (IPEndPoint)socket.Client.RemoteEndPoint!;
            var hello = UdpProtocol.Encode(UdpProtocol.HandshakeFlag, 0, 0, 0, 0, 0, []);
            UdpProtocol.UdpDatagram response = default;
            bool accepted = false;
            for (var attempt = 0; attempt < 20 && !accepted; attempt++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                await socket.SendAsync(hello, hello.Length).ConfigureAwait(false);
                using var receiveWindow = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                receiveWindow.CancelAfter(TimeSpan.FromMilliseconds(500));
                try
                {
                    var result = await socket.ReceiveAsync(receiveWindow.Token).ConfigureAwait(false);
                    if (!UdpProtocol.TryDecode(result.Buffer, out response, out _)) continue;
                    accepted = response.IsHandshakeResponse && response.ConnectionId != 0;
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                if (!accepted) await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            }
            if (!accepted) throw new NetworkProtocolException("Reliable UDP handshake timed out or was rejected.");
            var peer = new ReliableUdpPeer(response.ConnectionId, remote,
                async (packet, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    await socket.SendAsync(packet, token).ConfigureAwait(false);
                });
            var connection = new ReliableUdpConnection(peer, () =>
            {
                socket.Dispose();
                return ValueTask.CompletedTask;
            });
            connection.SetReceiveLoop(ReceiveAsync(socket, peer));
            return connection;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task ReceiveAsync(UdpClient socket, ReliableUdpPeer peer)
    {
        try
        {
            while (!peer.IsClosed)
            {
                var result = await socket.ReceiveAsync().ConfigureAwait(false);
                await peer.ProcessDatagramAsync(result.Buffer).ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException) when (peer.IsClosed) { }
        catch (SocketException) when (peer.IsClosed) { }
        catch (OperationCanceledException) when (peer.IsClosed) { }
        catch { peer.Close(); }
    }

    private Task? receiveTask;
    private void SetReceiveLoop(Task task) => receiveTask = task;
    public Task<byte[]?> ReadFrameAsync(CancellationToken cancellationToken = default) => peer.ReadFrameAsync(cancellationToken);
    public Task WriteFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => peer.WriteFrameAsync(payload, cancellationToken);
    public Task SendAsync(ReadOnlyMemory<byte> payload, DeliveryMode mode, CancellationToken cancellationToken = default) => peer.SendAsync(payload, mode, cancellationToken);
    internal Task ProcessDatagramAsync(ReadOnlyMemory<byte> payload) => peer.ProcessDatagramAsync(payload);
    public void Close() => peer.Close();
    public async ValueTask DisposeAsync()
    {
        await peer.DisposeAsync().ConfigureAwait(false);
        if (closeTransport is not null) await closeTransport().ConfigureAwait(false);
        var receive = receiveTask ?? receiveLoop;
        if (receive is not null)
        {
            try { await receive.ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
            catch (OperationCanceledException) { }
        }
    }
}
