using AstraNet.Core;

namespace AstraNet.IntegrationTests;

public enum DamageKind : byte
{
    Ordinary = 1,
    Critical = 2
}

[NetworkMessage]
public struct DamageCommand
{
    public int Amount;
    public DamageKind Kind;
    public string Source;
}

[NetworkMessage]
public struct ChatMessage
{
    public int Sequence;
    public string Text;
}

[NetworkBehaviour]
public sealed class TestPlayer : NetworkBehaviourBase
{
    [SyncVar] public int Health = 100;
    [SyncVar] public string Name = "Player";
    [SyncVar] public DamageCommand LastDamage;

    public int DamageCalls;
    public int EffectCalls;
    public int EffectTotal;
    public int FinallyCalls;
    public int CaughtExceptions;
    public int ActiveBodies;
    public int ConcurrentEntries;

    [ServerRpc]
    public void Damage(int amount)
    {
        Health -= amount;
        Interlocked.Increment(ref DamageCalls);
    }

    [ServerRpc]
    public void YieldingDamage(int amount)
    {
        if (Interlocked.Increment(ref ActiveBodies) > 1)
            Interlocked.Increment(ref ConcurrentEntries);
        try
        {
            var before = Health;
            Thread.Sleep(2);
            Health = before - amount;
            Interlocked.Increment(ref DamageCalls);
        }
        finally
        {
            Interlocked.Decrement(ref ActiveBodies);
        }
    }

    [ServerRpc]
    public void Adjust(int amount)
    {
        var absolute = Math.Abs(amount);
        try
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount));
            if (absolute > 0)
                Health -= absolute;
            else
                Health += 1;
        }
        catch (ArgumentOutOfRangeException)
        {
            Interlocked.Increment(ref CaughtExceptions);
        }
        finally
        {
            Interlocked.Increment(ref FinallyCalls);
        }
    }

    [ServerRpc]
    public void Adjust(DamageCommand command)
    {
        var multiplier = command.Kind == DamageKind.Critical ? 2 : 1;
        Health -= command.Amount * multiplier;
        LastDamage = command;
        Interlocked.Increment(ref DamageCalls);
    }

    [ClientRpc]
    public void PlayDamageEffect(int amount)
    {
        Interlocked.Add(ref EffectTotal, amount);
        Interlocked.Increment(ref EffectCalls);
    }

    [ClientRpc]
    public void PlayDamageEffect(DamageCommand command)
    {
        Interlocked.Add(ref EffectTotal, command.Amount);
        Interlocked.Increment(ref EffectCalls);
    }
}
