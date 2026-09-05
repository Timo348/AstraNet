using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using AstraNet.Core;
using AstraNet.Runtime;
using Xunit;

namespace AstraNet.IntegrationTests;

public sealed class EndToEndTests
{
    [Fact]
    public async Task Two_clients_receive_authoritative_state_and_client_rpc_exactly_once()
    {
        await using var host = await TestHost.CreateAsync();

        Assert.NotEqual(host.ClientA.ConnectionId, host.ClientB.ConnectionId);
        Assert.NotEqual(0u, host.ClientA.ConnectionId);
        host.PlayerA.Damage(10);

        await EventuallyAsync(() => host.ServerPlayer.Health == 90);
        Assert.Equal(1, host.ServerPlayer.DamageCalls);
        Assert.Equal(100, host.PlayerA.Health);
        Assert.Equal(100, host.PlayerB.Health);
        Assert.Equal(0, host.PlayerA.DamageCalls);
        Assert.Equal(0, host.PlayerB.DamageCalls);

        host.ServerPlayer.Name = "Authoritative player";
        await host.Server.ReplicateAsync(TestHost.ObjectId);
        await EventuallyAsync(() => host.PlayerA.Health == 90 && host.PlayerB.Health == 90);
        Assert.Equal("Authoritative player", host.PlayerA.Name);
        Assert.Equal("Authoritative player", host.PlayerB.Name);

        host.ServerPlayer.PlayDamageEffect(10);
        await EventuallyAsync(() => host.PlayerA.EffectCalls == 1 && host.PlayerB.EffectCalls == 1);
        await Task.Delay(100);
        Assert.Equal(1, host.PlayerA.EffectCalls);
        Assert.Equal(1, host.PlayerB.EffectCalls);
        Assert.Equal(10, host.PlayerA.EffectTotal);
        Assert.Equal(10, host.PlayerB.EffectTotal);
        Assert.Equal(0, host.ServerPlayer.EffectCalls);
    }

    [Fact]
    public async Task Woven_overloads_struct_arguments_locals_branches_and_exception_handlers_work()
    {
        await using var host = await TestHost.CreateAsync();

        host.PlayerA.Adjust(-5);
        host.PlayerA.Adjust(0);
        host.PlayerA.Adjust(3);
        host.PlayerA.Adjust(new DamageCommand { Amount = 4, Kind = DamageKind.Critical, Source = "client A" });

        await EventuallyAsync(() => host.ServerPlayer.FinallyCalls == 3 && host.ServerPlayer.DamageCalls == 1);
        Assert.Equal(90, host.ServerPlayer.Health);
        Assert.Equal(1, host.ServerPlayer.CaughtExceptions);
        Assert.Equal("client A", host.ServerPlayer.LastDamage.Source);
        Assert.Equal(DamageKind.Critical, host.ServerPlayer.LastDamage.Kind);
        Assert.Equal(0, host.PlayerA.FinallyCalls);

        await host.Server.ReplicateAsync(TestHost.ObjectId);
        await EventuallyAsync(() => host.PlayerA.LastDamage.Amount == 4 && host.PlayerB.LastDamage.Amount == 4);
        Assert.Equal("client A", host.PlayerB.LastDamage.Source);
        Assert.Equal(90, host.PlayerB.Health);

        host.ServerPlayer.PlayDamageEffect(new DamageCommand { Amount = 7, Kind = DamageKind.Ordinary, Source = "server" });
        await EventuallyAsync(() => host.PlayerA.EffectCalls == 1 && host.PlayerB.EffectCalls == 1);
        Assert.Equal(7, host.PlayerA.EffectTotal);
        Assert.Equal(7, host.PlayerB.EffectTotal);
    }

    [Fact]
    public async Task Object_and_behaviour_identity_route_to_only_the_registered_instance()
    {
        await using var server = new NetworkServer();
        await using var client = new NetworkClient();
        var serverPlayers = new[] { new TestPlayer(), new TestPlayer(), new TestPlayer() };
        var clientPlayers = new[] { new TestPlayer(), new TestPlayer(), new TestPlayer() };
        var identities = new[] { (Object: 9u, Behaviour: (ushort)0), (Object: 9u, Behaviour: (ushort)1), (Object: 10u, Behaviour: (ushort)0) };
        for (var i = 0; i < identities.Length; i++)
        {
            server.RegisterBehaviour(identities[i].Object, identities[i].Behaviour, serverPlayers[i]);
            client.RegisterBehaviour(identities[i].Object, identities[i].Behaviour, clientPlayers[i]);
        }
        await server.StartAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync("127.0.0.1", server.Port);

        clientPlayers[1].Damage(17);
        clientPlayers[2].Damage(8);
        await EventuallyAsync(() => serverPlayers[1].Health == 83 && serverPlayers[2].Health == 92);
        Assert.Equal(100, serverPlayers[0].Health);
        await server.ReplicateAsync(9);
        await EventuallyAsync(() => clientPlayers[1].Health == 83);
        Assert.Equal(100, clientPlayers[0].Health);
        Assert.Equal(100, clientPlayers[2].Health);
        await server.ReplicateAsync(10);
        await EventuallyAsync(() => clientPlayers[2].Health == 92);
    }

    [Fact]
    public async Task Typed_messages_support_client_send_targeted_server_send_and_broadcast()
    {
        await using var host = await TestHost.CreateAsync();
        var inbound = new TaskCompletionSource<(uint Sender, ChatMessage Message)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedA = new ConcurrentQueue<ChatMessage>();
        var receivedB = new ConcurrentQueue<ChatMessage>();
        host.Server.OnMessage<ChatMessage>(100, (connection, message) => inbound.TrySetResult((connection.Id, message)));
        host.ClientA.OnMessage<ChatMessage>(101, receivedA.Enqueue);
        host.ClientB.OnMessage<ChatMessage>(101, receivedB.Enqueue);

        await host.ClientA.SendAsync(100, new ChatMessage { Sequence = 1, Text = "hello server ✓" });
        var request = await inbound.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(host.ClientA.ConnectionId, request.Sender);
        Assert.Equal("hello server ✓", request.Message.Text);

        await host.Server.SendAsync(host.ClientA.ConnectionId, 101, new ChatMessage { Sequence = 2, Text = "only A" });
        await EventuallyAsync(() => receivedA.Count == 1);
        Assert.Empty(receivedB);
        await host.Server.BroadcastAsync(101, new ChatMessage { Sequence = 3, Text = "everyone" });
        await EventuallyAsync(() => receivedA.Count == 2 && receivedB.Count == 1);
        Assert.Equal(new[] { 2, 3 }, receivedA.Select(message => message.Sequence));
        Assert.Equal(3, Assert.Single(receivedB).Sequence);
    }

    [Fact]
    public async Task Concurrent_clients_and_sends_preserve_complete_frames_and_unique_messages()
    {
        await using var host = await TestHost.CreateAsync();
        var seen = new ConcurrentDictionary<int, uint>();
        var receiveA = new ConcurrentDictionary<int, byte>();
        var receiveB = new ConcurrentDictionary<int, byte>();
        host.Server.OnMessage<ChatMessage>(100, (connection, message) =>
        {
            Assert.Equal(new string((char)('a' + message.Sequence % 20), 200), message.Text);
            Assert.True(seen.TryAdd(message.Sequence, connection.Id), $"Duplicate message {message.Sequence}");
        });
        host.ClientA.OnMessage<ChatMessage>(101, message => receiveA.TryAdd(message.Sequence, 0));
        host.ClientB.OnMessage<ChatMessage>(101, message => receiveB.TryAdd(message.Sequence, 0));

        await Task.WhenAll(Enumerable.Range(0, 80).Select(i =>
            (i % 2 == 0 ? host.ClientA : host.ClientB).SendAsync(100,
                new ChatMessage { Sequence = i, Text = new string((char)('a' + i % 20), 200) })));
        await EventuallyAsync(() => seen.Count == 80);
        Assert.Equal(40, seen.Count(pair => pair.Value == host.ClientA.ConnectionId));
        Assert.Equal(40, seen.Count(pair => pair.Value == host.ClientB.ConnectionId));

        await Task.WhenAll(Enumerable.Range(0, 48).Select(i =>
            host.Server.BroadcastAsync(101, new ChatMessage { Sequence = i, Text = "broadcast" })));
        await EventuallyAsync(() => receiveA.Count == 48 && receiveB.Count == 48);
    }

    [Fact]
    public async Task Disconnect_cleans_up_and_later_clients_remain_functional()
    {
        await using var host = await TestHost.CreateAsync();
        var disconnected = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Server.ClientDisconnected += connection => disconnected.TrySetResult(connection.Id);
        var oldId = host.ClientA.ConnectionId;

        await host.ClientA.DisconnectAsync();
        Assert.Equal(oldId, await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await EventuallyAsync(() => host.Server.ConnectionCount == 1);
        Assert.False(host.ClientA.IsConnected);

        await using var replacement = new NetworkClient();
        var player = new TestPlayer();
        replacement.RegisterBehaviour(TestHost.ObjectId, 0, player);
        await replacement.ConnectAsync("127.0.0.1", host.Server.Port);
        Assert.NotEqual(oldId, replacement.ConnectionId);
        Assert.NotEqual(host.ClientB.ConnectionId, replacement.ConnectionId);
        player.Damage(6);
        await EventuallyAsync(() => host.ServerPlayer.Health == 94);
        await host.Server.ReplicateAsync(TestHost.ObjectId);
        await EventuallyAsync(() => player.Health == 94 && host.PlayerB.Health == 94);
        Assert.Equal(2, host.Server.ConnectionCount);
    }

    internal static async Task EventuallyAsync(Func<bool> condition, int timeoutMilliseconds = 5_000)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.ElapsedMilliseconds > timeoutMilliseconds)
                throw new TimeoutException($"Network condition was not reached within {timeoutMilliseconds} ms.");
            await Task.Delay(10);
        }
    }

    internal sealed class TestHost : IAsyncDisposable
    {
        internal const uint ObjectId = 42;
        internal NetworkServer Server { get; } = new();
        internal NetworkClient ClientA { get; } = new();
        internal NetworkClient ClientB { get; } = new();
        internal TestPlayer ServerPlayer { get; } = new();
        internal TestPlayer PlayerA { get; } = new();
        internal TestPlayer PlayerB { get; } = new();

        internal static async Task<TestHost> CreateAsync()
        {
            var host = new TestHost();
            try
            {
                host.Server.RegisterBehaviour(ObjectId, 0, host.ServerPlayer);
                host.ClientA.RegisterBehaviour(ObjectId, 0, host.PlayerA);
                host.ClientB.RegisterBehaviour(ObjectId, 0, host.PlayerB);
                await host.Server.StartAsync(IPAddress.Loopback, 0);
                await host.ClientA.ConnectAsync("127.0.0.1", host.Server.Port);
                await host.ClientB.ConnectAsync("127.0.0.1", host.Server.Port);
                await EventuallyAsync(() => host.Server.ConnectionCount == 2);
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Task.WhenAll(ClientA.DisposeAsync().AsTask(), ClientB.DisposeAsync().AsTask(), Server.DisposeAsync().AsTask())
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
