using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading.Channels;
using AstraNet.Core;

namespace AstraNet.Transport;

/// <summary>Length-prefixed TCP frames. A single bounded writer preserves accepted send order.</summary>
public sealed class TcpFrameConnection : INetworkFrameConnection
{
    public const int DefaultMaxFrameLength = 1024 * 1024;
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly Channel<PendingWrite> _writes;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _enqueueLock = new();
    private readonly Task _writer;
    private readonly int _maxFrameLength;
    private int _closed;
    private int _reading;
    private Exception? _failure;

    public TcpFrameConnection(TcpClient client, uint connectionId = 0,
        int maxFrameLength = DefaultMaxFrameLength, int maxPendingWrites = 64)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrameLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPendingWrites);
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
        Id = connectionId;
        _maxFrameLength = maxFrameLength;
        _writes = Channel.CreateBounded<PendingWrite>(new BoundedChannelOptions(maxPendingWrites)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _writer = RunWriterAsync();
    }

    public uint Id { get; }
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Returns null only for a clean EOF between frames. Truncated frames are protocol errors.</summary>
    public async Task<byte[]?> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _reading, 1) != 0)
            throw new InvalidOperationException("Only one frame reader may run at a time.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            var header = new byte[4];
            if (!await ReadExactlyAsync(header, allowCleanEof: true, linked.Token).ConfigureAwait(false))
            {
                Close();
                return null;
            }
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length <= 0 || length > _maxFrameLength)
                throw new NetworkProtocolException($"Frame length {length} is outside 1..{_maxFrameLength}.");
            var payload = new byte[length];
            await ReadExactlyAsync(payload, allowCleanEof: false, linked.Token).ConfigureAwait(false);
            return payload;
        }
        catch
        {
            // Cancellation may occur after consuming a partial header or body. Never reuse that stream.
            Close();
            throw;
        }
        finally
        {
            Volatile.Write(ref _reading, 0);
        }
    }

    private async Task<bool> ReadExactlyAsync(Memory<byte> buffer, bool allowCleanEof, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await _stream.ReadAsync(buffer[offset..], token).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0 && allowCleanEof) return false;
                throw new NetworkProtocolException("Connection ended in a partial TCP frame.");
            }
            offset += read;
        }
        return true;
    }

    /// <summary>Completes after the entire frame is written. A full queue fails immediately.</summary>
    public Task WriteFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (payload.Length <= 0 || payload.Length > _maxFrameLength)
            throw new NetworkProtocolException($"Frame length {payload.Length} is outside 1..{_maxFrameLength}.");
        lock (_enqueueLock)
        {
            if (IsClosed) throw new IOException("Connection is closed.", _failure);
            var pending = new PendingWrite(payload.ToArray(), cancellationToken);
            if (!_writes.Writer.TryWrite(pending))
                throw new NetworkBackpressureException("The bounded TCP send queue is full.");
            return pending.Completion.Task;
        }
    }

    public Task SendAsync(ReadOnlyMemory<byte> payload, DeliveryMode mode, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        // TCP's stream contract is always reliable and ordered; the mode is
        // accepted so gameplay code can select transports without branching.
        return WriteFrameAsync(payload, cancellationToken);
    }

    private async Task RunWriterAsync()
    {
        Exception? failure = null;
        try
        {
            await foreach (var pending in _writes.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (pending.CancellationToken.IsCancellationRequested)
                {
                    pending.Completion.TrySetCanceled(pending.CancellationToken);
                    continue;
                }
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                        _lifetime.Token, pending.CancellationToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    var header = new byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(header, pending.Payload.Length);
                    await _stream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
                    await _stream.WriteAsync(pending.Payload, deadline.Token).ConfigureAwait(false);
                    pending.Completion.TrySetResult();
                }
                catch (Exception error)
                {
                    pending.Completion.TrySetException(error);
                    throw;
                }
            }
        }
        catch (Exception error)
        {
            failure = error;
            _failure = error;
        }
        finally
        {
            Close();
            while (_writes.Reader.TryRead(out var pending))
                pending.Completion.TrySetException(failure ?? new IOException("Connection closed before send completed."));
        }
    }

    /// <summary>Aborts outstanding socket operations and completes pending sends with an error.</summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _writes.Writer.TryComplete();
        _lifetime.Cancel();
        _client.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        await _writer.ConfigureAwait(false);
    }

    private sealed record PendingWrite(byte[] Payload, CancellationToken CancellationToken)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed class NetworkBackpressureException(string message) : IOException(message);
