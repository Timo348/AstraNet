using System.Buffers.Binary;
using AstraNet.Core;
using AstraNet.Transport;
using Xunit;

namespace AstraNet.UnitTests;

public sealed class ReliableUdpTests
{
    [Fact]
    public async Task Deterministic_loss_retransmits_1000_messages_in_order_without_duplicates()
    {
        await using var link = new DeterministicUdpNetwork(new DeterministicUdpNetworkOptions
        {
            LossPercent = 10,
            DuplicatePercent = 5,
            ReorderPercent = 35,
            BaseLatencyMilliseconds = 1,
            JitterMilliseconds = 2,
            Seed = 0x1000_0001u
        });
        using var sendDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        for (var first = 1; first <= 1_000; first += 24)
        {
            var sends = Enumerable.Range(first, Math.Min(24, 1_001 - first))
                .Select(sequence => link.Left.SendAsync(BitConverter.GetBytes(sequence),
                    DeliveryMode.ReliableOrdered, sendDeadline.Token))
                .ToArray();
            await Task.WhenAll(sends);
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        for (var expected = 1; expected <= 1_000; expected++)
        {
            var frame = await link.Right.ReadFrameAsync(deadline.Token);
            Assert.NotNull(frame);
            var reader = new NetworkReader(frame!);
            Assert.Equal(expected, reader.ReadInt32());
            reader.EnsureEnd();
        }
        Assert.Null(link.Failure);
        Assert.True(link.DroppedDatagrams > 0);
        Assert.True(link.DuplicatedDatagrams > 0);
        Assert.True(link.ReorderedPairs > 0);
    }

    [Fact]
    public async Task Reliable_sequence_wrap_skips_zero_and_preserves_order()
    {
        await using var link = new DeterministicUdpNetwork(new DeterministicUdpNetworkOptions
        {
            LossPercent = 10,
            DuplicatePercent = 20,
            ReorderPercent = 100,
            BaseLatencyMilliseconds = 1,
            JitterMilliseconds = 1,
            Seed = 0x1000_0002u
        }, initialSequence: uint.MaxValue - 1);
        await Task.WhenAll(
            link.Left.SendAsync(new byte[] { 1 }, DeliveryMode.ReliableOrdered),
            link.Left.SendAsync(new byte[] { 2 }, DeliveryMode.ReliableOrdered),
            link.Left.SendAsync(new byte[] { 3 }, DeliveryMode.ReliableOrdered))
            .WaitAsync(TimeSpan.FromSeconds(10));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        for (var expected = 1; expected <= 3; expected++)
            Assert.Equal(new[] { (byte)expected }, await link.Right.ReadFrameAsync(deadline.Token));
        Assert.Null(link.Failure);
    }

    [Fact]
    public async Task Unreliable_packets_are_not_retransmitted_after_deterministic_loss()
    {
        await using var link = new DeterministicUdpNetwork(new DeterministicUdpNetworkOptions
        {
            LossPercent = 30,
            BaseLatencyMilliseconds = 1,
            JitterMilliseconds = 2,
            Seed = 0x1000_0003u
        });
        for (var sequence = 1; sequence <= 100; sequence++)
            await link.Left.SendAsync(BitConverter.GetBytes(sequence), DeliveryMode.Unreliable);
        await Task.Delay(500);

        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var received = 0;
        while (true)
        {
            try
            {
                if (await link.Right.ReadFrameAsync(deadline.Token) is null) break;
                received++;
            }
            catch (OperationCanceledException) { break; }
        }
        Assert.InRange(received, 1, 99);
        Assert.True(link.DroppedDatagrams > 0);
        Assert.Null(link.Failure);
    }

    [Fact]
    public void Datagram_validation_rejects_bad_lengths_flags_and_handshakes()
    {
        var packet = UdpProtocol.Encode(0, 7, 3, 0, 0, UdpProtocol.ReliableOrderedChannel, new byte[] { 9 });
        Assert.True(UdpProtocol.TryDecode(packet, out var decoded, out var error), error);
        Assert.Equal(7u, decoded.ConnectionId);
        Assert.Equal(new byte[] { 9 }, decoded.Payload);

        Assert.False(UdpProtocol.TryDecode(packet[..^1], out _, out _));
        var unknownFlags = (byte[])packet.Clone();
        unknownFlags[3] = 0x80;
        Assert.False(UdpProtocol.TryDecode(unknownFlags, out _, out _));
        var responseOnly = (byte[])packet.Clone();
        responseOnly[3] = (byte)UdpProtocol.HandshakeResponseFlag;
        Assert.False(UdpProtocol.TryDecode(responseOnly, out _, out _));
        var ackWithPayload = UdpProtocol.Encode(UdpProtocol.AckOnlyFlag, 7, 0, 3, 0,
            UdpProtocol.ReliableOrderedChannel, new byte[] { 1 });
        Assert.False(UdpProtocol.TryDecode(ackWithPayload, out _, out _));
        var badHandshake = UdpProtocol.Encode(UdpProtocol.HandshakeFlag, 0, 0, 0, 0, 0, []);
        BinaryPrimitives.WriteUInt32LittleEndian(badHandshake.AsSpan(8), 1);
        Assert.False(UdpProtocol.TryDecode(badHandshake, out _, out _));
    }

    [Fact]
    public void Ack_tracker_handles_wraparound_and_duplicate_packets()
    {
        var tracker = new UdpAckTracker();
        Assert.True(tracker.Mark(uint.MaxValue - 1));
        Assert.True(tracker.Mark(uint.MaxValue));
        Assert.True(tracker.Mark(1));
        Assert.False(tracker.Mark(uint.MaxValue));
        Assert.True(tracker.IsAcked(uint.MaxValue - 1));
        Assert.True(tracker.IsAcked(uint.MaxValue));
        Assert.True(tracker.IsAcked(1));
    }

}
