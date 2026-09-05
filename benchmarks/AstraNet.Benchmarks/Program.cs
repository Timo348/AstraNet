using AstraNet.Core;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

BenchmarkRunner.Run<SerializationBenchmarks>();

[MemoryDiagnoser]
[ShortRunJob]
public class SerializationBenchmarks
{
    private readonly byte[] largeBytes = new byte[4096];
    private readonly byte[] smallPayload;
    private readonly byte[] largePayload;
    private readonly byte[] stringPayload;
    private readonly byte[] integerPayload;
    private readonly string playerName = "Player-Timo-東京";
    private readonly NetworkWriter reusableWriter = new(8192);
    private readonly NetworkReader reusableReader;

    public SerializationBenchmarks()
    {
        for (var i = 0; i < largeBytes.Length; i++) largeBytes[i] = (byte)i;
        var initial = new NetworkWriter();
        initial.WriteUInt32(42);
        initial.WriteUInt16(3);
        initial.WriteUInt32(0x12345678);
        initial.WriteSingle(10.5f);
        initial.WriteSingle(-4.25f);
        initial.WriteSingle(2.0f);
        initial.WriteSingle(0.0f);
        initial.WriteSingle(0.707f);
        initial.WriteSingle(0.0f);
        initial.WriteInt32(87);
        initial.WriteString(playerName);
        smallPayload = initial.ToArray();

        var stringWriter = new NetworkWriter();
        stringWriter.WriteString(playerName);
        stringPayload = stringWriter.ToArray();

        var large = new NetworkWriter();
        large.WriteUInt32(42);
        large.WriteBytes(largeBytes);
        largePayload = large.ToArray();
        integerPayload = [42, 0, 0, 0, 0x12, 0xef, 0xcd, 0xab, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x7f];
        reusableReader = new NetworkReader(integerPayload);
    }

    [Benchmark(Baseline = true)]
    public byte[] WriteIntegersLegacy()
    {
        var writer = new NetworkWriter();
        writer.WriteInt32(42);
        writer.WriteUInt32(0xabcdef12);
        writer.WriteInt64(long.MaxValue);
        return writer.ToArray();
    }

    [Benchmark]
    public int WriteIntegersReusable()
    {
        reusableWriter.Reset();
        reusableWriter.WriteInt32(42);
        reusableWriter.WriteUInt32(0xabcdef12);
        reusableWriter.WriteInt64(long.MaxValue);
        return reusableWriter.Length;
    }

    [Benchmark]
    public byte[] WriteRepresentativeRpc()
    {
        var writer = new NetworkWriter();
        writer.WriteUInt32(42);
        writer.WriteUInt16(3);
        writer.WriteUInt32(0x12345678);
        writer.WriteSingle(10.5f);
        writer.WriteSingle(-4.25f);
        writer.WriteSingle(2.0f);
        writer.WriteSingle(0.707f);
        writer.WriteInt32(87);
        writer.WriteString(playerName);
        return writer.ToArray();
    }

    [Benchmark]
    public int WriteRepresentativeRpcReusable()
    {
        reusableWriter.Reset();
        reusableWriter.WriteUInt32(42);
        reusableWriter.WriteUInt16(3);
        reusableWriter.WriteUInt32(0x12345678);
        reusableWriter.WriteSingle(10.5f);
        reusableWriter.WriteSingle(-4.25f);
        reusableWriter.WriteSingle(2.0f);
        reusableWriter.WriteSingle(0.707f);
        reusableWriter.WriteInt32(87);
        reusableWriter.WriteString(playerName);
        return reusableWriter.Length;
    }

    [Benchmark]
    public byte[] WriteString()
    {
        var writer = new NetworkWriter();
        writer.WriteString(playerName);
        return writer.ToArray();
    }

    [Benchmark]
    public int WriteStringReusable()
    {
        reusableWriter.Reset();
        reusableWriter.WriteString(playerName);
        return reusableWriter.Length;
    }

    [Benchmark]
    public byte[] WriteByteArray()
    {
        var writer = new NetworkWriter();
        writer.WriteBytes(largeBytes);
        return writer.ToArray();
    }

    [Benchmark]
    public int WriteByteArrayReusable()
    {
        reusableWriter.Reset();
        reusableWriter.WriteBytes(largeBytes.AsSpan());
        return reusableWriter.Length;
    }

    [Benchmark]
    public int ReadIntegersLegacy()
    {
        var reader = new NetworkReader(integerPayload);
        return reader.ReadInt32() + unchecked((int)reader.ReadUInt32()) + (int)reader.ReadInt64();
    }

    [Benchmark]
    public int ReadIntegersReusable()
    {
        reusableReader.Reset(integerPayload);
        return reusableReader.ReadInt32() + unchecked((int)reusableReader.ReadUInt32()) + (int)reusableReader.ReadInt64();
    }

    [Benchmark]
    public string? ReadString()
    {
        var reader = new NetworkReader(stringPayload);
        return reader.ReadString();
    }

    [Benchmark]
    public byte[]? ReadByteArray()
    {
        var reader = new NetworkReader(largePayload);
        return reader.ReadBytes();
    }

    [Benchmark]
    public int ReadByteArrayBorrowed()
    {
        reusableReader.Reset(largePayload);
        var bytes = reusableReader.ReadBytesMemory();
        return bytes?.Length ?? -1;
    }

    [Benchmark]
    public string? ReadRepresentativeRpc()
    {
        var reader = new NetworkReader(smallPayload);
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt16();
        _ = reader.ReadUInt32();
        _ = reader.ReadSingle();
        _ = reader.ReadSingle();
        _ = reader.ReadSingle();
        _ = reader.ReadSingle();
        _ = reader.ReadSingle();
        _ = reader.ReadSingle();
        _ = reader.ReadInt32();
        return reader.ReadString();
    }

    [Benchmark]
    public int ReadRepresentativeRpcReusable()
    {
        reusableReader.Reset(smallPayload);
        _ = reusableReader.ReadUInt32();
        _ = reusableReader.ReadUInt16();
        _ = reusableReader.ReadUInt32();
        _ = reusableReader.ReadSingle();
        _ = reusableReader.ReadSingle();
        _ = reusableReader.ReadSingle();
        _ = reusableReader.ReadSingle();
        _ = reusableReader.ReadSingle();
        _ = reusableReader.ReadSingle();
        _ = reusableReader.ReadInt32();
        var length = reusableReader.ReadInt32();
        reusableReader.ReadRemainingMemory();
        return length;
    }
}
