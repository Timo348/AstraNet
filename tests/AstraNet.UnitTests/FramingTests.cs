using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using AstraNet.Core;
using AstraNet.Transport;
using Xunit;

namespace AstraNet.UnitTests;

public sealed class FramingTests
{
    private static async Task<(TcpClient Sender, TcpFrameConnection Receiver)> PairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var sender = new TcpClient { NoDelay = true };
            var connecting = sender.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var receiver = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await connecting.WaitAsync(TimeSpan.FromSeconds(5));
            return (sender, new TcpFrameConnection(receiver));
        }
        finally { listener.Stop(); }
    }

    private static byte[] Frame(byte[] payload)
    {
        byte[] frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    [Fact]
    public async Task FragmentedHeaderAndPayloadAreReassembled()
    {
        var pair = await PairAsync();
        using var sender = pair.Sender;
        await using var receiver = pair.Receiver;
        byte[] payload = Enumerable.Range(0, 173).Select(i => (byte)i).ToArray();
        byte[] frame = Frame(payload);
        var pending = receiver.ReadFrameAsync();
        for (int i = 0; i < frame.Length; i++)
        {
            await sender.GetStream().WriteAsync(frame.AsMemory(i, 1));
            if (i == 1 || i == 12)
            {
                await Task.Delay(20);
                Assert.False(pending.IsCompleted);
            }
        }
        Assert.Equal(payload, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task MultipleFramesInSingleWriteRemainSeparate()
    {
        var pair = await PairAsync();
        using var sender = pair.Sender;
        await using var receiver = pair.Receiver;
        byte[][] payloads = [[1, 2], [3], [4, 5, 6, 7]];
        await sender.GetStream().WriteAsync(payloads.SelectMany(Frame).ToArray());
        foreach (var expected in payloads)
            Assert.Equal(expected, await receiver.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(1048577)]
    public async Task MalformedFrameLengthsAreRejected(int length)
    {
        var pair = await PairAsync();
        using var sender = pair.Sender;
        await using var receiver = pair.Receiver;
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        await sender.GetStream().WriteAsync(header);
        await Assert.ThrowsAsync<NetworkProtocolException>(() => receiver.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DisconnectBetweenFramesReturnsNull()
    {
        var pair = await PairAsync();
        await using var receiver = pair.Receiver;
        pair.Sender.Dispose();
        Assert.Null(await receiver.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    public async Task DisconnectInsideHeaderOrPayloadIsProtocolError(int bytesSent)
    {
        var pair = await PairAsync();
        await using var receiver = pair.Receiver;
        await pair.Sender.GetStream().WriteAsync(Frame([1, 2, 3, 4]).AsMemory(0, bytesSent));
        pair.Sender.Dispose();
        await Assert.ThrowsAsync<NetworkProtocolException>(() => receiver.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ConcurrentWritesNeverInterleaveFrames()
    {
        var pair = await PairAsync();
        await using var receiver = pair.Receiver;
        await using var sender = new TcpFrameConnection(pair.Sender);
        const int count = 32;
        var receiving = Task.Run(async () =>
        {
            var results = new List<byte[]>();
            for (int i = 0; i < count; i++) results.Add((await receiver.ReadFrameAsync())!);
            return results;
        });
        await Task.WhenAll(Enumerable.Range(0, count).Select(i => sender.WriteFrameAsync(Enumerable.Repeat((byte)i, 8192).ToArray()))).WaitAsync(TimeSpan.FromSeconds(5));
        var frames = await receiving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(count, frames.Select(frame => frame[0]).Distinct().Count());
        foreach (byte[] frame in frames)
        {
            Assert.Equal(8192, frame.Length);
            Assert.All(frame, value => Assert.Equal(frame[0], value));
        }
    }

    [Fact]
    public async Task CancellationEndsAnIdleRead()
    {
        var pair = await PairAsync();
        using var sender = pair.Sender;
        await using var receiver = pair.Receiver;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiver.ReadFrameAsync(cancel.Token));
    }
}
