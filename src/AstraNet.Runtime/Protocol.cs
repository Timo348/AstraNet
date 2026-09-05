using AstraNet.Core;

namespace AstraNet.Runtime;

internal enum PacketKind : byte { Hello = 1, UserMessage = 2, ServerRpc = 3, ClientRpc = 4, State = 5 }

internal static class Protocol
{
    internal static byte[] Message<T>(uint messageId, T value)
    {
        var writer = new NetworkWriter();
        writer.WriteByte((byte)PacketKind.UserMessage);
        writer.WriteUInt32(messageId);
        NetworkSerializer<T>.Write(writer, value);
        return writer.ToArray();
    }

    internal static byte[] Rpc(NetworkBehaviourBase behaviour, uint rpcId, bool serverRpc, byte[] payload)
    {
        var writer = new NetworkWriter();
        writer.WriteByte((byte)(serverRpc ? PacketKind.ServerRpc : PacketKind.ClientRpc));
        writer.WriteUInt32(behaviour.ObjectId);
        writer.WriteUInt16(behaviour.BehaviourId);
        writer.WriteUInt32(rpcId);
        writer.WriteRaw(payload);
        return writer.ToArray();
    }
}
