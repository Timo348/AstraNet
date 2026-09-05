using AstraNet.Core;
using Xunit;

namespace AstraNet.UnitTests;

public sealed class AllocationTests
{
    [Fact]
    public void ReusedWriterPrimitivePathAllocatesZeroBytes()
    {
        var writer = new NetworkWriter(new byte[64]);
        for (var i = 0; i < 100; i++) WritePrimitives(writer, i);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (var i = 0; i < 10_000; i++)
        {
            WritePrimitives(writer, i);
            checksum ^= writer.WrittenSpan[0];
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(checksum);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ReusedReaderPrimitiveAndMemorySlicePathAllocatesZeroBytes()
    {
        var payload = new byte[32];
        var seed = new NetworkWriter(payload);
        seed.WriteInt32(7);
        seed.WriteSingle(2.5f);
        seed.WriteBytes(new byte[] { 1, 2, 3, 4 });
        var reader = new NetworkReader(payload.AsMemory(0, seed.Length));
        for (var i = 0; i < 100; i++) ReadPrimitives(reader, payload.AsMemory(0, seed.Length));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (var i = 0; i < 10_000; i++) checksum ^= ReadPrimitives(reader, payload.AsMemory(0, seed.Length));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(checksum);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ReadBytesMemoryBorrowsInputWithoutCopying()
    {
        var payload = new byte[16];
        var writer = new NetworkWriter(payload);
        writer.WriteBytes(new byte[] { 7, 8, 9 });
        var reader = new NetworkReader(payload.AsMemory(0, writer.Length));
        var slice = reader.ReadBytesMemory();
        Assert.True(slice.HasValue);
        Assert.Equal(new byte[] { 7, 8, 9 }, slice.Value.ToArray());
        payload[4] = 42;
        Assert.Equal(42, slice.Value.Span[0]);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void ResetReusesCapacityAndPooledWriterReturnsSafely()
    {
        var writer = new NetworkWriter(8);
        writer.WriteUInt64(1);
        int initialCapacity = writer.Capacity;
        writer.Reset();
        writer.WriteByte(2);
        Assert.Equal(initialCapacity, writer.Capacity);
        using (var rented = NetworkWriter.Rent(64))
        {
            rented.WriteInt32(123);
            Assert.Equal(4, rented.Length);
        }
        writer.Dispose();
        writer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => writer.WriteByte(1));
    }

    private static void WritePrimitives(NetworkWriter writer, int value)
    {
        writer.Reset();
        writer.WriteInt32(value);
        writer.WriteUInt64(unchecked((ulong)value * 17));
        writer.WriteSingle(value + 0.5f);
    }

    private static int ReadPrimitives(NetworkReader reader, ReadOnlyMemory<byte> payload)
    {
        reader.Reset(payload);
        var value = reader.ReadInt32();
        value ^= (int)reader.ReadUInt64();
        value ^= BitConverter.SingleToInt32Bits(reader.ReadSingle());
        return value;
    }
}
