using System.Diagnostics;
using System.Net;
using AstraNet.Example.Shared;
using AstraNet.Runtime;

if (args.Contains("--demo"))
{
    await RunDemoAsync();
    return;
}

var portIndex = Array.IndexOf(args, "--port");
var port = portIndex >= 0 ? int.Parse(args[portIndex + 1]) : 7777;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
await using var server = new NetworkServer();
var authoritative = new PlayerState { Name = "Player" };
server.RegisterBehaviour(1, 0, authoritative);
server.ClientConnected += connection => Console.WriteLine($"Client {connection.Id} connected.");
server.ClientDisconnected += connection => Console.WriteLine($"Client {connection.Id} disconnected.");
server.Error += (_, error) => Console.Error.WriteLine($"Connection error: {error.Message}");
await server.StartAsync(IPAddress.Loopback, port);
Console.WriteLine($"Server listening on 127.0.0.1:{server.Port}. Health = {authoritative.Health}. Ctrl+C stops.");
var previousHealth = authoritative.Health;
try
{
    while (!cancellation.IsCancellationRequested)
    {
        // A full snapshot also initializes clients that joined after the last change.
        await server.ReplicateAsync(1, cancellation.Token);
        var health = authoritative.Health;
        if (health < previousHealth)
            authoritative.PlayDamageEffect(previousHealth - health);
        previousHealth = health;
        await Task.Delay(100, cancellation.Token);
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.WriteLine("Server stopping.");
}

static async Task RunDemoAsync()
{
    await using var server = new NetworkServer();
    await using var clientA = new NetworkClient();
    await using var clientB = new NetworkClient();
    var serverPlayer = new PlayerState { Name = "Player" };
    var playerA = new PlayerState();
    var playerB = new PlayerState();
    server.RegisterBehaviour(1, 0, serverPlayer);
    clientA.RegisterBehaviour(1, 0, playerA);
    clientB.RegisterBehaviour(1, 0, playerB);

    await server.StartAsync(IPAddress.Loopback, 0);
    Console.WriteLine($"Server starts on 127.0.0.1:{server.Port}.");
    await clientA.ConnectAsync("127.0.0.1", server.Port);
    Console.WriteLine($"Client A connects (connection {clientA.ConnectionId}).");
    await clientB.ConnectAsync("127.0.0.1", server.Port);
    Console.WriteLine($"Client B connects (connection {clientB.ConnectionId}).");
    Console.WriteLine($"Player starts with Health = {serverPlayer.Health}.");

    Console.WriteLine("Client A invokes Damage(15).");
    playerA.Damage(15);
    await UntilAsync(() => serverPlayer.Health == 85);
    if (playerA.Health != 100 || playerB.Health != 100)
        throw new InvalidOperationException("Client state changed before server replication.");

    await server.ReplicateAsync(1);
    await UntilAsync(() => playerA.Health == 85 && playerB.Health == 85);
    Console.WriteLine($"Client A sees Health = {playerA.Health}.");
    Console.WriteLine($"Client B sees Health = {playerB.Health}.");

    Console.WriteLine("Server invokes PlayDamageEffect(15).");
    serverPlayer.PlayDamageEffect(15);
    await UntilAsync(() => playerA.EffectCount == 1 && playerB.EffectCount == 1);
    await Task.Delay(100);
    if (playerA.EffectCount != 1 || playerB.EffectCount != 1 || serverPlayer.EffectCount != 0)
        throw new InvalidOperationException("ClientRpc delivery count was incorrect.");
    Console.WriteLine("DEMO PASSED: real TCP ServerRpc, SyncVar replication, and exactly one ClientRpc per client.");
}

static async Task UntilAsync(Func<bool> condition)
{
    var timer = Stopwatch.StartNew();
    while (!condition())
    {
        if (timer.Elapsed > TimeSpan.FromSeconds(5))
            throw new TimeoutException("The demonstration did not receive the expected network result.");
        await Task.Delay(10);
    }
}
