using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using AstraNet.Core;
using AstraNet.Runtime;
using AstraNet.Transport;
using Xunit;

namespace AstraNet.IntegrationTests;

public sealed class ProtocolSafetyTests
{
    [Theory]
    [InlineData("unknown-object")]
    [InlineData("unknown-behaviour")]
    [InlineData("unknown-rpc")]
    [InlineData("truncated-rpc")]
    [InlineData("trailing-rpc")]
    [InlineData("unknown-kind")]
    [InlineData("forged-client-rpc")]
    [InlineData("forged-state")]
    [InlineData("unknown-message")]
    [InlineData("truncated-message")]
    [InlineData("trailing-message")]
    public async Task Invalid_peer_is_closed_without_mutation_and_other_clients_keep_working(string attack)
    {
        await using var host = await EndToEndTests.TestHost.CreateAsync();
        var error = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var messageCalls = 0;
        host.Server.OnMessage<ChatMessage>(100, (_, _) => Interlocked.Increment(ref messageCalls));
        host.Server.Error += (_, exception) => error.TrySetResult(exception);
        using var rawSocket = new TcpClient();
        await rawSocket.ConnectAsync(IPAddress.Loopback, host.Server.Port);
        await using var peer = new TcpFrameConnection(rawSocket);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var hello = await peer.ReadFrameAsync(timeout.Token);
        Assert.NotNull(hello);
        Assert.Equal(1, hello[0]);
        await EndToEndTests.EventuallyAsync(() => host.Server.ConnectionCount == 3);

        await peer.WriteFrameAsync(BuildBadRequest(attack), timeout.Token);
        await error.Task.WaitAsync(timeout.Token);
        await EndToEndTests.EventuallyAsync(() => host.Server.ConnectionCount == 2);
        Assert.Equal(100, host.ServerPlayer.Health);
        Assert.Equal(0, host.ServerPlayer.DamageCalls);
        Assert.Equal(0, host.ServerPlayer.EffectCalls);
        Assert.Equal(0, messageCalls);

        host.PlayerB.Damage(1);
        await EndToEndTests.EventuallyAsync(() => host.ServerPlayer.Health == 99);
        await host.Server.ReplicateAsync(EndToEndTests.TestHost.ObjectId);
        await EndToEndTests.EventuallyAsync(() => host.PlayerA.Health == 99 && host.PlayerB.Health == 99);
        Assert.True(host.ClientA.IsConnected);
        Assert.True(host.ClientB.IsConnected);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Client_rejects_truncated_or_trailing_state_and_rpc_before_applying_them(bool rpc, bool trailing)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var client = new NetworkClient();
        var player = new TestPlayer();
        client.RegisterBehaviour(EndToEndTests.TestHost.ObjectId, 0, player);
        var error = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Error += exception => error.TrySetResult(exception);
        var connecting = client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
        using var socket = await listener.AcceptTcpClientAsync();
        await using var peer = new TcpFrameConnection(socket);
        var hello = new NetworkWriter();
        hello.WriteByte(1);
        hello.WriteUInt32(1);
        await peer.WriteFrameAsync(hello.ToArray());
        await connecting.WaitAsync(TimeSpan.FromSeconds(5));

        var writer = new NetworkWriter();
        writer.WriteByte(rpc ? (byte)4 : (byte)5);
        writer.WriteUInt32(EndToEndTests.TestHost.ObjectId);
        writer.WriteUInt16(0);
        if (rpc)
            writer.WriteUInt32(RpcId("PlayDamageEffect", "System.Int32"));
        if (trailing)
        {
            writer.WriteInt32(1);
            if (!rpc)
            {
                writer.WriteString("bad server state");
                writer.WriteInt32(10);
                writer.WriteByte((byte)DamageKind.Ordinary);
                writer.WriteString("forged");
            }
            writer.WriteByte(255);
        }
        else
        {
            // A full first state field exercises atomic decoding; RPC gets a partial int.
            if (!rpc) writer.WriteInt32(1);
            writer.WriteByte(0);
        }
        await peer.WriteFrameAsync(writer.ToArray());
        await error.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await EndToEndTests.EventuallyAsync(() => !client.IsConnected);
        Assert.Equal(100, player.Health);
        Assert.Equal("Player", player.Name);
        Assert.Equal(0, player.EffectCalls);
        Assert.Equal(0, player.DamageCalls);
    }

    private static byte[] BuildBadRequest(string attack)
    {
        var writer = new NetworkWriter();
        if (attack == "unknown-kind")
        {
            writer.WriteByte(255);
            return writer.ToArray();
        }
        if (attack.EndsWith("-message", StringComparison.Ordinal))
        {
            writer.WriteByte(2);
            writer.WriteUInt32(attack == "unknown-message" ? uint.MaxValue : 100);
            writer.WriteInt32(1);
            if (attack == "trailing-message") writer.WriteString("must not invoke the handler");
            writer.WriteByte(255);
            return writer.ToArray();
        }

        writer.WriteByte(attack switch { "forged-client-rpc" => 4, "forged-state" => 5, _ => (byte)3 });
        writer.WriteUInt32(attack == "unknown-object" ? uint.MaxValue : EndToEndTests.TestHost.ObjectId);
        writer.WriteUInt16(attack == "unknown-behaviour" ? ushort.MaxValue : (ushort)0);
        writer.WriteUInt32(attack == "unknown-rpc" ? 0 : RpcId("Damage", "System.Int32"));
        if (attack == "truncated-rpc")
            writer.WriteByte(10);
        else
            writer.WriteInt32(10);
        if (attack == "trailing-rpc") writer.WriteByte(255);
        return writer.ToArray();
    }

    // FNV-1a over the published canonical signature; raw peers exercise the public wire protocol.
    private static uint RpcId(string methodName, string parameterType)
    {
        var hash = 2166136261u;
        foreach (var value in Encoding.UTF8.GetBytes($"AstraNet.IntegrationTests.TestPlayer::{methodName}({parameterType})"))
            hash = unchecked((hash ^ value) * 16777619u);
        Assert.NotNull(typeof(TestPlayer).GetMethod($"__AstraNet_{methodName}_{hash:X8}_Impl",
            BindingFlags.Instance | BindingFlags.NonPublic));
        return hash;
    }
}
