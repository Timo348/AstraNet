using AstraNet.Core;

namespace AstraNet.Example.Shared;

[NetworkBehaviour]
public sealed class PlayerState : NetworkBehaviourBase
{
    [SyncVar] public int Health = 100;
    [SyncVar] public string Name = "Player";

    public int EffectCount;

    [ServerRpc]
    public void Damage(int amount)
    {
        if (amount < 0 || amount > 100)
            return;
        Health = Math.Max(0, Health - amount);
        Console.WriteLine($"Server received ServerRpc: {Name}.Health = {Health}");
    }

    [ClientRpc]
    public void PlayDamageEffect(int amount)
    {
        Interlocked.Increment(ref EffectCount);
        Console.WriteLine($"{Name}: damage effect {amount}; Health = {Health}");
    }
}
