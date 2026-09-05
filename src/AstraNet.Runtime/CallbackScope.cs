namespace AstraNet.Runtime;

// The active flag prevents a child Task from retaining a stale callback identity after its parent returns.
internal sealed class CallbackScope
{
    internal bool Active { get; set; } = true;

    internal static void Run(AsyncLocal<CallbackScope?> slot, Action callback)
    {
        var previous = slot.Value;
        var current = new CallbackScope();
        slot.Value = current;
        try { callback(); }
        finally
        {
            current.Active = false;
            slot.Value = previous;
        }
    }
}
