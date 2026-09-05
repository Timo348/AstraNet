using System.Collections.Concurrent;
using System.Net;
using AstraNet.Core;
using AstraNet.Runtime;
using AstraNet.Transport;
using Xunit;

namespace AstraNet.IntegrationTests;

public sealed class UdpEndToEndTests
{
    [Fact]
    public async Task Reliable_udp_runs_rpc_syncvar_and_typed_messages()
    {
        await using var server = new NetworkServer(NetworkTransportKind.ReliableUdp);
        await using var clientA = new NetworkClient(NetworkTransportKind.ReliableUdp);
        await using var clientB = new NetworkClient(NetworkTransportKind.ReliableUdp);
        var serverPlayer = new TestPlayer();
        var clientAPlayer = new TestPlayer();
        var clientBPlayer = new TestPlayer();
        server.RegisterBehaviour(77, 0, serverPlayer);
        clientA.RegisterBehaviour(77, 0, clientAPlayer);
        clientB.RegisterBehaviour(77, 0, clientBPlayer);
        var receivedA = new ConcurrentQueue<ChatMessage>();
        var receivedB = new ConcurrentQueue<ChatMessage>();
        var requests = new ConcurrentQueue<ChatMessage>();
        server.OnMessage<ChatMessage>(100, (_, message) => requests.Enqueue(message));
        clientA.OnMessage<ChatMessage>(101, receivedA.Enqueue);
        clientB.OnMessage<ChatMessage>(101, receivedB.Enqueue);

        await server.StartAsync(IPAddress.Loopback, 0);
        await clientA.ConnectAsync("127.0.0.1", server.Port);
        await clientB.ConnectAsync("127.0.0.1", server.Port);
        await EventuallyAsync(() => server.ConnectionCount == 2);
        Assert.True(clientA.IsConnected);
        Assert.True(clientB.IsConnected);
        Assert.NotEqual(clientA.ConnectionId, clientB.ConnectionId);

        clientAPlayer.Damage(9);
        await EventuallyAsync(() => serverPlayer.Health == 91);
        await clientA.SendAsync(100, new ChatMessage { Sequence = 9, Text = "message to server" });
        await EventuallyAsync(() => requests.Any(message => message.Sequence == 9));
        var inbound = requests.Single(message => message.Sequence == 9);
        Assert.Equal(9, inbound.Sequence);

        serverPlayer.Name = "UDP authoritative";
        await server.ReplicateAsync(77);
        await EventuallyAsync(() => clientAPlayer.Health == 91 && clientBPlayer.Health == 91);
        Assert.Equal("UDP authoritative", clientAPlayer.Name);
        Assert.Equal("UDP authoritative", clientBPlayer.Name);

        serverPlayer.PlayDamageEffect(10);
        await EventuallyAsync(() => clientAPlayer.EffectCalls == 1 && clientBPlayer.EffectCalls == 1);
        await Task.Delay(100);
        Assert.Equal(1, clientAPlayer.EffectCalls);
        Assert.Equal(1, clientBPlayer.EffectCalls);
        Assert.Equal(10, clientAPlayer.EffectTotal);
        Assert.Equal(10, clientBPlayer.EffectTotal);

        await server.SendAsync(clientA.ConnectionId, 101,
            new ChatMessage { Sequence = 10, Text = "reliable UDP" });
        await EventuallyAsync(() => receivedA.Count == 1);
        Assert.Equal("reliable UDP", Assert.Single(receivedA).Text);
        Assert.Empty(receivedB);

        await server.BroadcastAsync(101, new ChatMessage { Sequence = 12, Text = "broadcast UDP" });
        await EventuallyAsync(() => receivedA.Count == 2 && receivedB.Count == 1);
        Assert.Equal(new[] { 10, 12 }, receivedA.Select(message => message.Sequence));
        Assert.Equal(12, Assert.Single(receivedB).Sequence);

        await clientA.SendAsync(100, new ChatMessage { Sequence = 11, Text = "unreliable UDP" },
            DeliveryMode.Unreliable);
        await EventuallyAsync(() => requests.Any(message => message.Sequence == 11));
        var unreliable = requests.Single(message => message.Sequence == 11);
        Assert.Equal("unreliable UDP", unreliable.Text);

        var disconnected = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ClientDisconnected += connection => disconnected.TrySetResult(connection.Id);
        var connectionId = clientA.ConnectionId;
        await clientA.DisconnectAsync();
        Assert.Equal(connectionId, await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await EventuallyAsync(() => server.ConnectionCount == 1);
        Assert.True(clientB.IsConnected);
    }

    [Fact]
    public async Task Reliable_udp_server_reaps_idle_sessions()
    {
        await using var server = new ReliableUdpServer(4, TimeSpan.FromMilliseconds(100));
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ClientDisconnected += _ => disconnected.TrySetResult();
        await server.StartAsync(IPAddress.Loopback, 0);
        await using var client = await ReliableUdpConnection.ConnectAsync("127.0.0.1", server.Port);
        await using var accepted = await server.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(5))
            ?? throw new InvalidOperationException("UDP server did not accept the handshake.");
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, server.ConnectionCount);
        Assert.True(client.IsClosed);
    }

    private static async Task EventuallyAsync(Func<bool> condition, int timeoutMilliseconds = 5_000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("UDP condition was not reached.");
            await Task.Delay(10);
        }
    }
}
