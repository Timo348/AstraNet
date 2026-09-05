using AstraNet.Core;
using Xunit;

namespace AstraNet.UnitTests;

[NetworkSerializable]
public struct Coordinates
{
    public float X;
    public double Y;
    public int Z;
}

[NetworkSerializable]
public enum PlayerKind : ushort { None, Human = 513 }
[NetworkSerializable] public enum SignedKind : sbyte { Low = -128 }
[NetworkSerializable] public enum ByteKind : byte { High = 255 }
[NetworkSerializable] public enum ShortKind : short { Low = -32768 }
[NetworkSerializable] public enum IntKind : int { Low = int.MinValue }
[NetworkSerializable] public enum UIntKind : uint { High = uint.MaxValue }
[NetworkSerializable] public enum LongKind : long { Low = long.MinValue }
[NetworkSerializable] public enum ULongKind : ulong { High = ulong.MaxValue }

[NetworkSerializable]
public struct PlayerData
{
    public Coordinates Position;
    public PlayerKind Kind;
    public string? Name;
    public byte[]? Inventory;
    private long stamp;
    public long Stamp { readonly get => stamp; set => stamp = value; }
}

public sealed class SerializationTests
{
    public static bool NestedContainerInitialized;

    private static class PrivateContainer
    {
        static PrivateContainer() => NestedContainerInitialized = true;

        [NetworkSerializable]
        private struct DeepPayload { public int Value; }

        internal static int RoundTripDeep(int value) => RoundTrip(new DeepPayload { Value = value }).Value;
    }

    [NetworkSerializable]
    private struct PrivatePayload
    {
        public int Value;
        public string? Label;
    }

    private static T RoundTrip<T>(T value)
    {
        var writer = new NetworkWriter();
        NetworkSerializer<T>.Write(writer, value);
        var reader = new NetworkReader(writer.ToArray());
        var result = NetworkSerializer<T>.Read(reader);
        reader.EnsureEnd();
        return result;
    }

    [Fact]
    public void AllRequiredPrimitiveTypesRoundTrip()
    {
        Assert.Equal(byte.MaxValue, RoundTrip(byte.MaxValue));
        Assert.Equal(sbyte.MinValue, RoundTrip(sbyte.MinValue));
        Assert.True(RoundTrip(true));
        Assert.False(RoundTrip(false));
        Assert.Equal(short.MinValue, RoundTrip(short.MinValue));
        Assert.Equal(ushort.MaxValue, RoundTrip(ushort.MaxValue));
        Assert.Equal(int.MinValue, RoundTrip(int.MinValue));
        Assert.Equal(uint.MaxValue, RoundTrip(uint.MaxValue));
        Assert.Equal(long.MinValue, RoundTrip(long.MinValue));
        Assert.Equal(ulong.MaxValue, RoundTrip(ulong.MaxValue));
        Assert.Equal(-123.125f, RoundTrip(-123.125f));
        Assert.Equal(double.MaxValue, RoundTrip(double.MaxValue));
        Assert.Equal("Timo 🌍\0Grüße 東京", RoundTrip("Timo 🌍\0Grüße 東京"));
        Assert.Equal(new byte[] { 0, 1, 127, 255 }, RoundTrip(new byte[] { 0, 1, 127, 255 }));
    }

    [Fact]
    public void FloatingPointBitPatternsArePreserved()
    {
        foreach (int bits in new[] { 0, int.MinValue, 0x7f800000, unchecked((int)0xff800000), 0x7fc01234 })
            Assert.Equal(bits, BitConverter.SingleToInt32Bits(RoundTrip(BitConverter.Int32BitsToSingle(bits))));
        foreach (long bits in new[] { 0L, long.MinValue, 0x7ff0000000000000L, unchecked((long)0xfff0000000000000UL), 0x7ff8123456789abcL })
            Assert.Equal(bits, BitConverter.DoubleToInt64Bits(RoundTrip(BitConverter.Int64BitsToDouble(bits))));
    }

    [Fact]
    public void NullAndEmptyValuesRemainDistinct()
    {
        Assert.Null(RoundTrip<string?>(null));
        Assert.Equal("", RoundTrip(""));
        var serialized = new NetworkWriter();
        NetworkSerializer<byte[]?>.Write(serialized, null);
        Assert.Equal(-1, new NetworkReader(serialized.ToArray()).ReadInt32());
        Assert.Null(new NetworkReader(serialized.ToArray()).ReadBytes());
        Assert.Null(RoundTrip<byte[]?>(null));
        Assert.Empty(RoundTrip(Array.Empty<byte>()));
    }

    [Fact]
    public void TwoCustomStructsIncludingNestedAndPrivateFieldsRoundTrip()
    {
        var position = new Coordinates { X = 1.25f, Y = -8.75, Z = int.MaxValue };
        Assert.Equal(position, RoundTrip(position));
        var value = new PlayerData { Position = position, Kind = PlayerKind.Human, Name = "Ada", Inventory = [4, 5], Stamp = 9876543210 };
        var result = RoundTrip(value);
        Assert.Equal(value.Position, result.Position);
        Assert.Equal(value.Kind, result.Kind);
        Assert.Equal(value.Name, result.Name);
        Assert.Equal(value.Inventory, result.Inventory);
        Assert.Equal(value.Stamp, result.Stamp);
    }

    [Fact]
    public void NestedPrivateStructHasAnAccessibleGeneratedRegistration()
    {
        var value = new PrivatePayload { Value = 42, Label = "private nested" };
        var actual = RoundTrip(value);
        Assert.Equal(value.Value, actual.Value);
        Assert.Equal(value.Label, actual.Label);
    }

    [Fact]
    public void DeepPrivateStructRegistrationPreservesUserModuleInitializerAndTypeInitialization()
    {
        Assert.True(ConsumerInitialization.Ready);
        Assert.Equal(19, PrivateContainer.RoundTripDeep(19));
        Assert.True(NestedContainerInitialized);
    }

    [Fact]
    public void EveryEnumUnderlyingTypeRoundTrips()
    {
        Assert.Equal(PlayerKind.Human, RoundTrip(PlayerKind.Human));
        Assert.Equal(SignedKind.Low, RoundTrip(SignedKind.Low));
        Assert.Equal(ByteKind.High, RoundTrip(ByteKind.High));
        Assert.Equal(ShortKind.Low, RoundTrip(ShortKind.Low));
        Assert.Equal(IntKind.Low, RoundTrip(IntKind.Low));
        Assert.Equal(UIntKind.High, RoundTrip(UIntKind.High));
        Assert.Equal(LongKind.Low, RoundTrip(LongKind.Low));
        Assert.Equal(ULongKind.High, RoundTrip(ULongKind.High));
    }

    [Fact]
    public void EncodingUsesExplicitLittleEndianLengthsAndValues()
    {
        var writer = new NetworkWriter();
        writer.WriteUInt32(0x12345678);
        writer.WriteString("é");
        writer.WriteBytes(null);
        Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12, 2, 0, 0, 0, 0xc3, 0xa9, 255, 255, 255, 255 }, writer.ToArray());
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(int.MinValue)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void MalformedLengthPrefixesAreRejectedBeforeAllocation(int length)
    {
        var writer = new NetworkWriter();
        writer.WriteInt32(length);
        Assert.Throws<NetworkProtocolException>(() => new NetworkReader(writer.ToArray()).ReadString());
        Assert.Throws<NetworkProtocolException>(() => new NetworkReader(writer.ToArray()).ReadBytes());
    }

    [Fact]
    public void TruncationTrailingBytesAndInvalidBooleansAreRejected()
    {
        Assert.Throws<NetworkProtocolException>(() => new NetworkReader(new byte[3]).ReadInt32());
        Assert.Throws<NetworkProtocolException>(() => new NetworkReader(new byte[] { 2 }).ReadBool());
        Assert.Throws<NetworkProtocolException>(() => new NetworkReader(new byte[] { 0 }).EnsureEnd());
        Assert.Throws<NetworkProtocolException>(() => NetworkSerializer<PlayerData>.Read(new NetworkReader(new byte[4])));
    }

    [Fact]
    public void InvalidUnicodeIsRejected()
    {
        var writer = new NetworkWriter();
        writer.WriteInt32(2);
        writer.WriteRaw(new byte[] { 0xc0, 0xaf });
        Assert.Throws<NetworkProtocolException>(() => new NetworkReader(writer.ToArray()).ReadString());
        Assert.Throws<NetworkProtocolException>(() => new NetworkWriter().WriteString("\ud800"));
    }

    [Fact]
    public void SizeLimitsAreEnforcedOnReadAndWrite()
    {
        Assert.Throws<NetworkProtocolException>(() => new NetworkReader(new byte[NetworkLimits.MaxMessageSize + 1]));
        Assert.Throws<NetworkProtocolException>(() => new NetworkWriter().WriteBytes(new byte[NetworkLimits.MaxMessageSize]));
        var writer = new NetworkWriter();
        writer.WriteRaw(new byte[NetworkLimits.MaxMessageSize]);
        Assert.Throws<NetworkProtocolException>(() => writer.WriteByte(1));
        Assert.Equal(NetworkLimits.MaxMessageSize, writer.Length);
    }
}

internal static class ConsumerInitialization
{
    public static bool Ready;
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Initialize()
    {
        if (SerializationTests.NestedContainerInitialized)
            throw new InvalidOperationException("Serializer registration ran a user's enclosing static constructor.");
        var writer = new NetworkWriter();
        NetworkSerializer<Coordinates>.Write(writer, new Coordinates { Z = 17 });
        Ready = NetworkSerializer<Coordinates>.Read(new NetworkReader(writer.ToArray())).Z == 17;
    }
}
