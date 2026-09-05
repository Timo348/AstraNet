using System.Buffers.Binary;
using System.Text;

namespace AstraNet.Core;

/// <summary>
/// A bounded reader that can be reset over another payload. Primitive reads and
/// memory slices do not allocate; string and owning byte-array APIs necessarily do.
/// </summary>
public sealed class NetworkReader
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private ReadOnlyMemory<byte> data;
    private int position;

    public int Remaining => data.Length - position;
    public int Position => position;

    public NetworkReader(byte[] bytes) : this((ReadOnlyMemory<byte>)(bytes ?? throw new ArgumentNullException(nameof(bytes)))) { }
    public NetworkReader(ReadOnlyMemory<byte> bytes) => Reset(bytes);

    public void Reset(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > NetworkLimits.MaxMessageSize)
            throw new NetworkProtocolException("Message exceeds the 1 MiB limit.");
        data = bytes;
        position = 0;
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining) throw new NetworkProtocolException("Truncated or invalid payload length.");
        var value = data.Span.Slice(position, count);
        position += count;
        return value;
    }

    private ReadOnlyMemory<byte> TakeMemory(int count)
    {
        if (count < 0 || count > Remaining) throw new NetworkProtocolException("Truncated or invalid payload length.");
        var value = data.Slice(position, count);
        position += count;
        return value;
    }

    public byte ReadByte() => Take(1)[0];
    public sbyte ReadSByte() => unchecked((sbyte)ReadByte());
    public bool ReadBool() => ReadByte() switch { 0 => false, 1 => true, _ => throw new NetworkProtocolException("Boolean must be 0 or 1.") };
    public bool ReadBoolean() => ReadBool();
    public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public float ReadSingle() => BitConverter.Int32BitsToSingle(ReadInt32());
    public double ReadDouble() => BitConverter.Int64BitsToDouble(ReadInt64());

    private int ReadLength()
    {
        int length = ReadInt32();
        if (length < -1 || length > Remaining) throw new NetworkProtocolException("Invalid length prefix.");
        return length;
    }

    public string? ReadString()
    {
        int length = ReadLength();
        if (length == -1) return null;
        try { return Utf8.GetString(Take(length)); }
        catch (DecoderFallbackException e) { throw new NetworkProtocolException("Invalid UTF-8 payload.", e); }
    }

    /// <summary>Reads a non-owning slice. The returned memory is valid while the input buffer is retained.</summary>
    public ReadOnlyMemory<byte>? ReadBytesMemory()
    {
        int length = ReadLength();
        if (length == -1) return null;
        return TakeMemory(length);
    }

    public bool TryReadBytesMemory(out ReadOnlyMemory<byte> value)
    {
        var result = ReadBytesMemory();
        if (result is null) { value = default; return false; }
        value = result.Value;
        return true;
    }

    public byte[]? ReadBytes()
    {
        var memory = ReadBytesMemory();
        return memory is null ? null : memory.Value.ToArray();
    }

    public ReadOnlyMemory<byte> ReadRemainingMemory() => TakeMemory(Remaining);
    public byte[] ReadRemainingBytes() => ReadRemainingMemory().ToArray();
    public void EnsureEnd()
    {
        if (Remaining != 0) throw new NetworkProtocolException($"Unexpected {Remaining} trailing payload bytes.");
    }
}
