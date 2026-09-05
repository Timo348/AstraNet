using AstraNet.Example.Shared;
using AstraNet.Runtime;

var host = ReadOption("--host", "127.0.0.1");
var port = int.Parse(ReadOption("--port", "7777"));
var damage = int.Parse(ReadOption("--damage", "15"));
var seconds = int.Parse(ReadOption("--seconds", "10"));
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
await using var client = new NetworkClient();
var player = new PlayerState();
client.RegisterBehaviour(1, 0, player);
client.Error += error => Console.Error.WriteLine($"Connection error: {error.Message}");
client.Disconnected += () => Console.WriteLine("Disconnected from server.");
await client.ConnectAsync(host, port, cancellation.Token);
Console.WriteLine($"Client {client.ConnectionId} connected. Starting Health = {player.Health}.");
if (damage > 0)
{
    Console.WriteLine($"Invoking Damage({damage}) through the generated ServerRpc wrapper.");
    player.Damage(damage);
}

var previousHealth = player.Health;
try
{
    while (client.IsConnected && !cancellation.IsCancellationRequested)
    {
        if (player.Health != previousHealth)
        {
            previousHealth = player.Health;
            Console.WriteLine($"Replicated Health = {previousHealth}");
        }
        await Task.Delay(50, cancellation.Token);
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
}
Console.WriteLine($"Finished. Health = {player.Health}, damage effects = {player.EffectCount}.");
await client.DisconnectAsync();

string ReadOption(string option, string defaultValue)
{
    var index = Array.IndexOf(args, option);
    return index >= 0 ? args[index + 1] : defaultValue;
}
