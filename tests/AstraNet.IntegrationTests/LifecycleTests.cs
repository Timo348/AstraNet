using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AstraNet.Runtime;
using Xunit;

namespace AstraNet.IntegrationTests;

public sealed class LifecycleTests
{
    [Fact]
    public async Task Disposing_during_a_pending_hello_cancels_connection_promptly()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var client = new NetworkClient();
        var connectionTask = client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        // The peer deliberately withholds Hello; disposal must cancel the in-progress handshake.
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        var error = await Record.ExceptionAsync(() => connectionTask.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.NotNull(error);
        Assert.IsNotType<TimeoutException>(error);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Connecting_clients_receive_hello_before_concurrent_broadcasts_and_snapshots()
    {
        await using var server = new NetworkServer();
        server.RegisterBehaviour(1, 0, new TestPlayer { Health = 73 });
        var clients = Enumerable.Range(0, 12).Select(_ => new NetworkClient()).ToArray();
        var players = clients.Select(_ => new TestPlayer()).ToArray();
        var received = new ConcurrentDictionary<uint, int>();
        for (var i = 0; i < clients.Length; i++)
        {
            var client = clients[i];
            client.RegisterBehaviour(1, 0, players[i]);
            client.OnMessage<ChatMessage>(100, _ => received.AddOrUpdate(client.ConnectionId, 1, (_, value) => value + 1));
        }
        await server.StartAsync(IPAddress.Loopback, 0);
        using var cancellation = new CancellationTokenSource();
        var broadcasting = BroadcastContinuouslyAsync();
        try
        {
            await Task.WhenAll(clients.Select(client => client.ConnectAsync("127.0.0.1", server.Port)))
                .WaitAsync(TimeSpan.FromSeconds(5));
            await EndToEndTests.EventuallyAsync(() => received.Count == clients.Length && players.All(player => player.Health == 73));
            Assert.Equal(clients.Length, clients.Select(client => client.ConnectionId).Distinct().Count());
            Assert.All(clients, client => Assert.True(client.IsConnected));
            Assert.All(clients, client => Assert.Null(client.LastError));
            Assert.Null(server.LastError);
        }
        finally
        {
            cancellation.Cancel();
            await broadcasting;
            foreach (var client in clients) await client.DisposeAsync();
        }

        async Task BroadcastContinuouslyAsync()
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    await server.BroadcastAsync(100, new ChatMessage { Text = "traffic during handshake" }, cancellation.Token);
                    await server.ReplicateAsync(1, cancellation.Token);
                    await Task.Delay(1, cancellation.Token);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    [Fact]
    public async Task Concurrent_server_rpcs_serialize_authoritative_mutations_on_the_same_behaviour()
    {
        await using var host = await EndToEndTests.TestHost.CreateAsync();
        await Task.WhenAll(Task.Run(() => Send(host.PlayerA)), Task.Run(() => Send(host.PlayerB)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        await EndToEndTests.EventuallyAsync(() => host.ServerPlayer.DamageCalls == 120);
        Assert.Equal(-20, host.ServerPlayer.Health);
        Assert.Equal(0, host.ServerPlayer.ConcurrentEntries);
        await host.Server.ReplicateAsync(EndToEndTests.TestHost.ObjectId);
        await EndToEndTests.EventuallyAsync(() => host.PlayerA.Health == -20 && host.PlayerB.Health == -20);

        static void Send(TestPlayer player)
        {
            for (var i = 0; i < 60; i++) player.YieldingDamage(1);
        }
    }

    [Fact]
    public async Task Client_message_callback_can_disconnect_synchronously_without_awaiting_itself()
    {
        await using var host = await EndToEndTests.TestHost.CreateAsync();
        var callbackFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ClientA.OnMessage<ChatMessage>(100, _ =>
        {
            host.ClientA.DisconnectAsync().GetAwaiter().GetResult();
            callbackFinished.TrySetResult();
        });
        await host.Server.SendAsync(host.ClientA.ConnectionId, 100, new ChatMessage { Text = "disconnect now" });
        await callbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.ClientA.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await EndToEndTests.EventuallyAsync(() => host.Server.ConnectionCount == 1);
        Assert.False(host.ClientA.IsConnected);
        Assert.True(host.ClientB.IsConnected);
    }

    [Fact]
    public async Task Server_message_callback_can_dispose_synchronously_without_awaiting_itself()
    {
        await using var host = await EndToEndTests.TestHost.CreateAsync();
        var callbackFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Server.OnMessage<ChatMessage>(100, (_, _) =>
        {
            host.Server.DisposeAsync().GetAwaiter().GetResult();
            callbackFinished.TrySetResult();
        });
        await host.ClientA.SendAsync(100, new ChatMessage { Text = "stop server" });
        await callbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.Server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await EndToEndTests.EventuallyAsync(() => !host.ClientA.IsConnected && !host.ClientB.IsConnected);
        Assert.Equal(0, host.Server.ConnectionCount);
        Assert.False(host.ServerPlayer.IsAttached);
    }
}
