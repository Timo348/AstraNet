using System.Buffers.Binary;
using AstraNet.Core;

namespace AstraNet.Transport;

internal static class UdpProtocol
{
    internal const ushort Magic = 0x4153; // "SA" in little endian
    internal const byte Version = 1;
    internal const int HeaderSize = 23;
    internal const int DefaultDatagramSize = 1200;
    internal const int MaxPayloadSize = DefaultDatagramSize - HeaderSize;
    internal const int HandshakeFlag = 1;
    internal const int AckOnlyFlag = 2;
    internal const int HandshakeResponseFlag = 4;
    internal const int DisconnectFlag = 8;
    internal const byte ReliableOrderedChannel = 1;

    internal static byte[] Encode(byte flags, uint connectionId, uint sequence, uint ack, uint ackBits,
        byte channel, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadSize) throw new NetworkProtocolException($"UDP payload exceeds {MaxPayloadSize} bytes.");
        var packet = new byte[HeaderSize + payload.Length];
        var span = packet.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span, Magic);
        span[2] = Version;
        span[3] = flags;
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], connectionId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], ack);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], ackBits);
        span[20] = channel;
        BinaryPrimitives.WriteUInt16LittleEndian(span[21..], checked((ushort)payload.Length));
        payload.CopyTo(span[HeaderSize..]);
        return packet;
    }

    internal static bool TryDecode(ReadOnlySpan<byte> packet, out UdpDatagram datagram, out string? error)
    {
        datagram = default;
        error = null;
        if (packet.Length < HeaderSize) { error = "UDP datagram is shorter than its header."; return false; }
        if (BinaryPrimitives.ReadUInt16LittleEndian(packet) != Magic) { error = "UDP magic is invalid."; return false; }
        if (packet[2] != Version) { error = $"UDP protocol version {packet[2]} is unsupported."; return false; }
        byte flags = packet[3];
        if ((flags & ~(HandshakeFlag | AckOnlyFlag | HandshakeResponseFlag | DisconnectFlag)) != 0) { error = "UDP flags contain unknown bits."; return false; }
        if ((flags & HandshakeResponseFlag) != 0 && (flags & HandshakeFlag) == 0)
        {
            error = "UDP handshake response flag requires a handshake flag.";
            return false;
        }
        uint connectionId = BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]);
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(packet[8..]);
        uint ack = BinaryPrimitives.ReadUInt32LittleEndian(packet[12..]);
        uint ackBits = BinaryPrimitives.ReadUInt32LittleEndian(packet[16..]);
        byte channel = packet[20];
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(packet[21..]);
        if (length > MaxPayloadSize || packet.Length != HeaderSize + length)
        {
            error = "UDP payload length does not match datagram length.";
            return false;
        }
        if ((flags & AckOnlyFlag) != 0 && (length != 0 || sequence != 0 || channel != ReliableOrderedChannel ||
            (flags & ~AckOnlyFlag) != 0))
        {
            error = "ACK-only UDP datagram has invalid flags or a payload.";
            return false;
        }
        if ((flags & HandshakeFlag) != 0 && (sequence != 0 || ack != 0 || ackBits != 0 || channel != 0 || length != 0))
        {
            error = "Handshake UDP datagram must not carry data.";
            return false;
        }
        if ((flags & HandshakeFlag) != 0 && (flags & ~(HandshakeFlag | HandshakeResponseFlag)) != 0)
        {
            error = "Handshake UDP datagram has invalid flags.";
            return false;
        }
        if ((flags & DisconnectFlag) != 0 && (length != 0 || sequence != 0 || ack != 0 || ackBits != 0 || channel != 0))
        {
            error = "Disconnect UDP datagram must not carry data.";
            return false;
        }
        if ((flags & DisconnectFlag) != 0 && (flags & ~DisconnectFlag) != 0)
        {
            error = "Disconnect UDP datagram has invalid flags.";
            return false;
        }
        if (flags == 0 && ((channel == 0 && sequence != 0) ||
            (channel == ReliableOrderedChannel && sequence == 0)))
        {
            error = "UDP data sequence does not match its channel.";
            return false;
        }
        if ((flags & AckOnlyFlag) == 0 && (flags & HandshakeFlag) == 0 && channel != ReliableOrderedChannel && channel != 0)
        {
            error = "UDP channel is unsupported.";
            return false;
        }
        datagram = new UdpDatagram(flags, connectionId, sequence, ack, ackBits, channel,
            packet.Slice(HeaderSize, length).ToArray());
        return true;
    }

    internal readonly record struct UdpDatagram(byte Flags, uint ConnectionId, uint Sequence, uint Ack,
        uint AckBits, byte Channel, byte[] Payload)
    {
        internal bool IsHandshake => (Flags & HandshakeFlag) != 0;
        internal bool IsHandshakeResponse => (Flags & (HandshakeFlag | HandshakeResponseFlag)) ==
            (HandshakeFlag | HandshakeResponseFlag);
        internal bool IsAckOnly => (Flags & AckOnlyFlag) != 0;
        internal bool IsDisconnect => (Flags & DisconnectFlag) != 0;
    }
}

internal static class UdpSequence
{
    internal static bool IsNewer(uint left, uint right) => left != right && unchecked((int)(left - right)) > 0;
    internal static bool IsAtOrBefore(uint left, uint right) => left == right || !IsNewer(left, right);
}

internal struct UdpAckTracker
{
    internal bool HasLatest;
    internal uint Latest;
    internal uint Bits;

    internal bool Mark(uint sequence)
    {
        if (!HasLatest)
        {
            HasLatest = true;
            Latest = sequence;
            Bits = 0;
            return true;
        }
        if (sequence == Latest) return false;
        if (UdpSequence.IsNewer(sequence, Latest))
        {
            uint delta = sequence - Latest;
            Bits = delta > 32 ? 0 : delta == 32 ? 0x80000000u : (Bits << (int)delta) | (1u << (int)(delta - 1));
            Latest = sequence;
            return true;
        }
        uint behind = Latest - sequence;
        if (behind is >= 1 and <= 32)
        {
            uint bit = 1u << (int)(behind - 1);
            bool fresh = (Bits & bit) == 0;
            Bits |= bit;
            return fresh;
        }
        return false;
    }

    internal bool IsAcked(uint sequence)
    {
        if (!HasLatest) return false;
        if (sequence == Latest) return true;
        uint behind = Latest - sequence;
        return behind is >= 1 and <= 32 && (Bits & (1u << (int)(behind - 1))) != 0;
    }
}
