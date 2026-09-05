using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace AstraNet.Core;

/// <summary>
/// A bounded little-endian writer. A writer may be reset and reused; the reusable
/// path writes directly into its backing buffer and allocates nothing for primitive values.
/// </summary>
public sealed class NetworkWriter : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private byte[] buffer;
    private readonly ArrayPool<byte>? pool;
    private int position;
    private int disposed;

    /// <summary>Creates an owned writer with a small initial buffer.</summary>
    public NetworkWriter() : this(256) { }

    public NetworkWriter(int initialCapacity)
    {
        if (initialCapacity <= 0 || initialCapacity > NetworkLimits.MaxMessageSize)
            throw new ArgumentOutOfRangeException(nameof(initialCapacity));
        buffer = new byte[initialCapacity];
    }

    /// <summary>Wraps caller-owned storage. The storage must remain valid while writing.</summary>
    public NetworkWriter(byte[] storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (storage.Length == 0 || storage.Length > NetworkLimits.MaxMessageSize)
            throw new ArgumentOutOfRangeException(nameof(storage));
        buffer = storage;
    }

    private NetworkWriter(byte[] storage, ArrayPool<byte> pool)
    {
        buffer = storage;
        this.pool = pool;
    }

    /// <summary>Rents backing storage; dispose the writer to return it to the pool.</summary>
    public static NetworkWriter Rent(int minimumCapacity = 256)
    {
        if (minimumCapacity <= 0 || minimumCapacity > NetworkLimits.MaxMessageSize)
            throw new ArgumentOutOfRangeException(nameof(minimumCapacity));
        return new NetworkWriter(ArrayPool<byte>.Shared.Rent(minimumCapacity), ArrayPool<byte>.Shared);
    }

    public int Length => position;
    public int Capacity => buffer.Length;
    public ReadOnlySpan<byte> WrittenSpan
    {
        get { ThrowIfDisposed(); return buffer.AsSpan(0, position); }
    }
    public ReadOnlyMemory<byte> WrittenMemory
    {
        get { ThrowIfDisposed(); return buffer.AsMemory(0, position); }
    }

    /// <summary>Clears the logical contents without clearing or reallocating backing storage.</summary>
    public void Reset()
    {
        ThrowIfDisposed();
        position = 0;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    }

    private Span<byte> Reserve(int count)
    {
        ThrowIfDisposed();
        if (count < 0 || count > NetworkLimits.MaxMessageSize - position)
            throw new NetworkProtocolException("Message exceeds the 1 MiB limit.");
        EnsureCapacity(position + count);
        return buffer.AsSpan(position, count);
    }

    private void EnsureCapacity(int required)
    {
        if (required <= buffer.Length) return;
        int next = Math.Max(required, Math.Min(NetworkLimits.MaxMessageSize, Math.Max(buffer.Length * 2, 256)));
        if (next < required || next > NetworkLimits.MaxMessageSize)
            throw new NetworkProtocolException("Message exceeds the 1 MiB limit.");
        if (pool is null)
        {
            Array.Resize(ref buffer, next);
            return;
        }
        var replacement = pool.Rent(next);
        buffer.AsSpan(0, position).CopyTo(replacement);
        pool.Return(buffer);
        buffer = replacement;
    }

    public void WriteByte(byte value) { Reserve(1)[0] = value; position += 1; }
    public void WriteSByte(sbyte value) => WriteByte(unchecked((byte)value));
    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);
    public void WriteBoolean(bool value) => WriteBool(value);
    public void WriteInt16(short value) { BinaryPrimitives.WriteInt16LittleEndian(Reserve(2), value); position += 2; }
    public void WriteUInt16(ushort value) { BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), value); position += 2; }
    public void WriteInt32(int value) { BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value); position += 4; }
    public void WriteUInt32(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value); position += 4; }
    public void WriteInt64(long value) { BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value); position += 8; }
    public void WriteUInt64(ulong value) { BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), value); position += 8; }
    public void WriteSingle(float value) => WriteInt32(BitConverter.SingleToInt32Bits(value));
    public void WriteDouble(double value) => WriteInt64(BitConverter.DoubleToInt64Bits(value));

    public void WriteString(string? value)
    {
        if (value is null) { WriteInt32(-1); return; }
        int length;
        try { length = Utf8.GetByteCount(value.AsSpan()); }
        catch (EncoderFallbackException e) { throw new NetworkProtocolException("Invalid UTF-16 string.", e); }
        if (length > NetworkLimits.MaxMessageSize - position - 4)
            throw new NetworkProtocolException("String exceeds the message limit.");
        WriteInt32(length);
        try { Utf8.GetBytes(value.AsSpan(), Reserve(length)); }
        catch (EncoderFallbackException e) { throw new NetworkProtocolException("Invalid UTF-16 string.", e); }
        position += length;
    }

    public void WriteBytes(byte[]? value)
    {
        if (value is null) { WriteInt32(-1); return; }
        WriteBytes(value.AsSpan());
    }

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        if (value.Length > NetworkLimits.MaxMessageSize - position - 4)
            throw new NetworkProtocolException("Byte array exceeds the message limit.");
        WriteInt32(value.Length);
        WriteRaw(value);
    }

    public void WriteRaw(ReadOnlySpan<byte> value)
    {
        value.CopyTo(Reserve(value.Length));
        position += value.Length;
    }

    /// <summary>Creates an owned copy for asynchronous transport. Use WrittenSpan for synchronous consumers.</summary>
    public byte[] ToArray() => WrittenSpan.ToArray();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (pool is not null)
        {
            pool.Return(buffer);
            buffer = Array.Empty<byte>();
        }
        position = 0;
    }
}
