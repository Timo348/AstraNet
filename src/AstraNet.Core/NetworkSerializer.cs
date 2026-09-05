using System.Runtime.CompilerServices;

namespace AstraNet.Core;

/// <summary>Typed delegates installed by generated module initializers, without reflection.</summary>
public static class NetworkSerializer<T>
{
    public static Action<NetworkWriter, T>? Writer;
    public static Func<NetworkReader, T>? Reader;

    public static void Write(NetworkWriter writer, T value) =>
        (Writer ?? throw new InvalidOperationException($"No serializer for {typeof(T)}. Enable AstraNet weaving and mark the struct [NetworkSerializable]."))(writer, value);

    public static T Read(NetworkReader reader) =>
        (Reader ?? throw new InvalidOperationException($"No serializer for {typeof(T)}. Enable AstraNet weaving and mark the struct [NetworkSerializable]."))(reader);
}

internal static class PrimitiveSerializers
{
#pragma warning disable CA2255 // A library module initializer installs the fixed primitive codec table.
    [ModuleInitializer]
    internal static void Initialize()
    {
        Register<byte>((w, v) => w.WriteByte(v), r => r.ReadByte());
        Register<sbyte>((w, v) => w.WriteSByte(v), r => r.ReadSByte());
        Register<bool>((w, v) => w.WriteBool(v), r => r.ReadBool());
        Register<short>((w, v) => w.WriteInt16(v), r => r.ReadInt16());
        Register<ushort>((w, v) => w.WriteUInt16(v), r => r.ReadUInt16());
        Register<int>((w, v) => w.WriteInt32(v), r => r.ReadInt32());
        Register<uint>((w, v) => w.WriteUInt32(v), r => r.ReadUInt32());
        Register<long>((w, v) => w.WriteInt64(v), r => r.ReadInt64());
        Register<ulong>((w, v) => w.WriteUInt64(v), r => r.ReadUInt64());
        Register<float>((w, v) => w.WriteSingle(v), r => r.ReadSingle());
        Register<double>((w, v) => w.WriteDouble(v), r => r.ReadDouble());
        Register<string?>((w, v) => w.WriteString(v), r => r.ReadString());
        Register<byte[]?>(WriteNullableBytes, ReadNullableBytes);
    }
#pragma warning restore CA2255
    private static void Register<T>(Action<NetworkWriter, T> write, Func<NetworkReader, T> read)
    {
        NetworkSerializer<T>.Writer = write;
        NetworkSerializer<T>.Reader = read;
    }

    private static void WriteNullableBytes(NetworkWriter writer, byte[]? value) => writer.WriteBytes(value);
    private static byte[]? ReadNullableBytes(NetworkReader reader) => reader.ReadBytes();
}
