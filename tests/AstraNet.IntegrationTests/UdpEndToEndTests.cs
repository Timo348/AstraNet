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
        await using var client = new NetworkClient(NetworkTransportKind.ReliableUdp);
        var serverPlayer = new TestPlayer();
        var clientPlayer = new TestPlayer();
        server.RegisterBehaviour(77, 0, serverPlayer);
        client.RegisterBehaviour(77, 0, clientPlayer);
        var received = new ConcurrentQueue<ChatMessage>();
        var requests = new ConcurrentQueue<ChatMessage>();
        server.OnMessage<ChatMessage>(100, (_, message) => requests.Enqueue(message));
        client.OnMessage<ChatMessage>(101, received.Enqueue);

        await server.StartAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync("127.0.0.1", server.Port);
        Assert.True(client.IsConnected);

        clientPlayer.Damage(9);
        await EventuallyAsync(() => serverPlayer.Health == 91);
        await client.SendAsync(100, new ChatMessage { Sequence = 9, Text = "message to server" });
        await EventuallyAsync(() => requests.Any(message => message.Sequence == 9));
        var inbound = requests.Single(message => message.Sequence == 9);
        Assert.Equal(9, inbound.Sequence);

        serverPlayer.Name = "UDP authoritative";
        await server.ReplicateAsync(77);
        await EventuallyAsync(() => clientPlayer.Health == 91);
        Assert.Equal("UDP authoritative", clientPlayer.Name);

        await server.SendAsync(client.ConnectionId, 101,
            new ChatMessage { Sequence = 10, Text = "reliable UDP" });
        await EventuallyAsync(() => received.Count == 1);
        Assert.Equal("reliable UDP", Assert.Single(received).Text);

        await client.SendAsync(100, new ChatMessage { Sequence = 11, Text = "unreliable UDP" },
            DeliveryMode.Unreliable);
        await EventuallyAsync(() => requests.Any(message => message.Sequence == 11));
        var unreliable = requests.Single(message => message.Sequence == 11);
        Assert.Equal("unreliable UDP", unreliable.Text);

        var disconnected = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ClientDisconnected += connection => disconnected.TrySetResult(connection.Id);
        var connectionId = client.ConnectionId;
        await client.DisconnectAsync();
        Assert.Equal(connectionId, await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5)));
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
