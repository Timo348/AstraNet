using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace AstraNet.Core;

/// <summary>Bounded, little endian encoding with strict UTF-8 and explicit lengths.</summary>
public sealed class NetworkWriter
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly ArrayBufferWriter<byte> buffer = new();
    public int Length => buffer.WrittenCount;

    private Span<byte> Reserve(int count)
    {
        if (count < 0 || count > NetworkLimits.MaxMessageSize - Length)
            throw new NetworkProtocolException("Message exceeds the 1 MiB limit.");
        return buffer.GetSpan(count)[..count];
    }

    public void WriteByte(byte value) { Reserve(1)[0] = value; buffer.Advance(1); }
    public void WriteSByte(sbyte value) => WriteByte(unchecked((byte)value));
    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);
    public void WriteInt16(short value) { BinaryPrimitives.WriteInt16LittleEndian(Reserve(2), value); buffer.Advance(2); }
    public void WriteUInt16(ushort value) { BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), value); buffer.Advance(2); }
    public void WriteInt32(int value) { BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value); buffer.Advance(4); }
    public void WriteUInt32(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value); buffer.Advance(4); }
    public void WriteInt64(long value) { BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value); buffer.Advance(8); }
    public void WriteUInt64(ulong value) { BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), value); buffer.Advance(8); }
    public void WriteSingle(float value) => WriteInt32(BitConverter.SingleToInt32Bits(value));
    public void WriteDouble(double value) => WriteInt64(BitConverter.DoubleToInt64Bits(value));

    public void WriteString(string? value)
    {
        if (value is null) { WriteInt32(-1); return; }
        int length;
        try { length = Utf8.GetByteCount(value); }
        catch (EncoderFallbackException e) { throw new NetworkProtocolException("Invalid UTF-16 string.", e); }
        if (length > NetworkLimits.MaxMessageSize - Length - 4)
            throw new NetworkProtocolException("String exceeds the message limit.");
        WriteInt32(length);
        Utf8.GetBytes(value.AsSpan(), Reserve(length));
        buffer.Advance(length);
    }

    public void WriteBytes(byte[]? value)
    {
        if (value is null) { WriteInt32(-1); return; }
        if (value.Length > NetworkLimits.MaxMessageSize - Length - 4)
            throw new NetworkProtocolException("Byte array exceeds the message limit.");
        WriteInt32(value.Length);
        WriteRaw(value);
    }

    public void WriteRaw(ReadOnlySpan<byte> value)
    {
        value.CopyTo(Reserve(value.Length));
        buffer.Advance(value.Length);
    }

    public byte[] ToArray() => buffer.WrittenSpan.ToArray();
}
