using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using AstraNet.Transport;

namespace AstraNet.UnitTests;

/// <summary>Deterministic in-memory datagram link used by the reliable-UDP tests.</summary>
internal sealed record DeterministicUdpNetworkOptions
{
    public int LossPercent { get; init; }
    public int DuplicatePercent { get; init; }
    public int ReorderPercent { get; init; }
    public int BaseLatencyMilliseconds { get; init; }
    public int JitterMilliseconds { get; init; }
    public int ReorderDelayMilliseconds { get; init; } = 2;
    public uint Seed { get; init; } = 0xA57A_2026u;

    internal void Validate()
    {
        if (LossPercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(LossPercent));
        if (DuplicatePercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(DuplicatePercent));
        if (ReorderPercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(ReorderPercent));
        if (BaseLatencyMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(BaseLatencyMilliseconds));
        if (JitterMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(JitterMilliseconds));
        if (ReorderDelayMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(ReorderDelayMilliseconds));
    }
}

internal sealed class DeterministicUdpNetwork : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Direction leftToRight;
    private readonly Direction rightToLeft;
    private Exception? failure;
    private int droppedDatagrams;
    private int duplicatedDatagrams;
    private int reorderedPairs;

    public DeterministicUdpNetwork(DeterministicUdpNetworkOptions options, uint initialSequence = 1)
    {
        options.Validate();
        leftToRight = new(options, this, 0x1357_9BDFu);
        rightToLeft = new(options, this, 0x2468_ACE0u);
        Left = new ReliableUdpPeer(7, new IPEndPoint(IPAddress.Loopback, 10001),
            (packet, token) => leftToRight.EnqueueAsync(packet, token), initialSequence);
        Right = new ReliableUdpPeer(7, new IPEndPoint(IPAddress.Loopback, 10002),
            (packet, token) => rightToLeft.EnqueueAsync(packet, token), initialSequence);
        leftToRight.Target = Right;
        rightToLeft.Target = Left;
        leftToRight.Start();
        rightToLeft.Start();
    }

    public ReliableUdpPeer Left { get; }
    public ReliableUdpPeer Right { get; }
    public Exception? Failure => failure;
    public int DroppedDatagrams => Volatile.Read(ref droppedDatagrams);
    public int DuplicatedDatagrams => Volatile.Read(ref duplicatedDatagrams);
    public int ReorderedPairs => Volatile.Read(ref reorderedPairs);

    private void RecordDropped() => Interlocked.Increment(ref droppedDatagrams);
    private void RecordDuplicated() => Interlocked.Increment(ref duplicatedDatagrams);
    private void RecordReordered() => Interlocked.Increment(ref reorderedPairs);

    private void RecordFailure(Exception error)
    {
        Interlocked.CompareExchange(ref failure, error, null);
        Left.Close();
        Right.Close();
    }

    public async ValueTask DisposeAsync()
    {
        Left.Close();
        Right.Close();
        lifetime.Cancel();
        leftToRight.Complete();
        rightToLeft.Complete();
        await Left.DisposeAsync();
        await Right.DisposeAsync();
        await Task.WhenAll(leftToRight.Completion, rightToLeft.Completion);
        lifetime.Dispose();
    }

    private sealed class Direction
    {
        private readonly DeterministicUdpNetworkOptions options;
        private readonly DeterministicUdpNetwork owner;
        private readonly uint seed;
        private readonly Channel<ScheduledDatagram> input = Channel.CreateUnbounded<ScheduledDatagram>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
        private int ordinal;
        private Task? pump;

        internal Direction(DeterministicUdpNetworkOptions options, DeterministicUdpNetwork owner, uint seed)
        {
            this.options = options;
            this.owner = owner;
            this.seed = seed;
        }

        internal ReliableUdpPeer? Target { get; set; }
        internal Task Completion => pump ?? Task.CompletedTask;

        internal void Start() => pump = PumpAsync();

        internal ValueTask EnqueueAsync(ReadOnlyMemory<byte> packet, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            int number = Interlocked.Increment(ref ordinal);
            if (Pick(number, 11, options.LossPercent))
            {
                owner.RecordDropped();
                return ValueTask.CompletedTask;
            }
            var datagram = Create(packet, number, duplicate: false);
            input.Writer.TryWrite(datagram);
            if (Pick(number, 29, options.DuplicatePercent))
            {
                owner.RecordDuplicated();
                input.Writer.TryWrite(Create(packet, number, duplicate: true));
            }
            return ValueTask.CompletedTask;
        }

        internal void Complete() => input.Writer.TryComplete();

        private ScheduledDatagram Create(ReadOnlyMemory<byte> packet, int number, bool duplicate)
        {
            int jitter = options.JitterMilliseconds == 0
                ? 0
                : (int)(Hash(number, 47) % (uint)(options.JitterMilliseconds + 1));
            int delay = options.BaseLatencyMilliseconds + jitter;
            int pair = (number + 1) / 2;
            bool reversePair = Pick(pair, 71, options.ReorderPercent);
            if (reversePair && (number & 1) == 1)
            {
                owner.RecordReordered();
                delay += options.ReorderDelayMilliseconds;
            }
            if (duplicate) delay += options.ReorderDelayMilliseconds;
            long ticks = Stopwatch.GetTimestamp() + (long)delay * Stopwatch.Frequency / 1_000;
            return new ScheduledDatagram(packet.ToArray(), ticks, number, duplicate);
        }

        private async Task PumpAsync()
        {
            var ready = new PriorityQueue<ScheduledDatagram, (long Due, int Number)>();
            try
            {
                while (true)
                {
                    while (input.Reader.TryRead(out var queued))
                        ready.Enqueue(queued, (queued.DueTicks, queued.Number));
                    if (ready.Count == 0)
                    {
                        if (!await input.Reader.WaitToReadAsync(owner.lifetime.Token).ConfigureAwait(false)) break;
                        continue;
                    }
                    var next = ready.Peek();
                    long remaining = next.DueTicks - Stopwatch.GetTimestamp();
                    if (remaining > 0)
                    {
                        var waitForInput = input.Reader.WaitToReadAsync(owner.lifetime.Token).AsTask();
                        var waitForDue = Task.Delay(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency), owner.lifetime.Token);
                        await Task.WhenAny(waitForInput, waitForDue).ConfigureAwait(false);
                        continue;
                    }
                    var datagram = ready.Dequeue();
                    await (Target ?? throw new InvalidOperationException("UDP simulation target is not connected.")
                        ).ProcessDatagramAsync(datagram.Payload).ConfigureAwait(false);
                }
                while (ready.TryDequeue(out var remainingDatagram, out _))
                    await (Target ?? throw new InvalidOperationException("UDP simulation target is not connected.")
                        ).ProcessDatagramAsync(remainingDatagram.Payload).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (owner.lifetime.IsCancellationRequested) { }
            catch (Exception error) { owner.RecordFailure(error); }
        }

        private uint Hash(int value, uint salt)
        {
            unchecked
            {
                uint x = seed ^ (uint)value * 0x9E37_79B9u ^ salt;
                x ^= x >> 16;
                x *= 0x85EB_CA6Bu;
                x ^= x >> 13;
                x *= 0xC2B2_AE35u;
                return x ^ (x >> 16);
            }
        }

        private bool Pick(int value, uint salt, int percent) => percent > 0 && Hash(value, salt) % 100u < percent;

        private readonly record struct ScheduledDatagram(byte[] Payload, long DueTicks, int Number, bool Duplicate);
    }
}
