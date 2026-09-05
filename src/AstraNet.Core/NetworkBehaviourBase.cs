namespace AstraNet.Core;

/// <summary>The runtime sends only generated numeric RPC identifiers.</summary>
public interface INetworkContext
{
    bool IsServer { get; }
    void SendRpc(NetworkBehaviourBase behaviour, uint rpcId, bool serverRpc, byte[] payload);
}

/// <summary>Derive directly and mark the class [NetworkBehaviour] to opt into weaving.</summary>
public abstract class NetworkBehaviourBase
{
    private INetworkContext? context;
    public uint ObjectId { get; private set; }
    public ushort BehaviourId { get; private set; }
    public bool IsServer => context?.IsServer ?? false;
    public bool IsAttached => context is not null;

    public void Attach(INetworkContext networkContext, uint objectId, ushort behaviourId)
    {
        ArgumentNullException.ThrowIfNull(networkContext);
        if (objectId == 0) throw new ArgumentOutOfRangeException(nameof(objectId));
        if (context is not null) throw new InvalidOperationException("Behaviour is already registered.");
        ObjectId = objectId;
        BehaviourId = behaviourId;
        context = networkContext;
    }

    public void Detach()
    {
        context = null;
        ObjectId = 0;
        BehaviourId = 0;
    }

    // Public because consumer assemblies contain the generated calls and overrides.
    public bool __AstraNet_ShouldSend(bool serverRpc)
    {
        var attached = context ?? throw new InvalidOperationException("Register the behaviour before invoking an RPC.");
        if (serverRpc) return !attached.IsServer;
        if (!attached.IsServer) throw new InvalidOperationException("Only the server can invoke a ClientRpc.");
        return true;
    }

    public void __AstraNet_SendRpc(uint rpcId, bool serverRpc, NetworkWriter writer)
    {
        var attached = context ?? throw new InvalidOperationException("Behaviour is not registered.");
        attached.SendRpc(this, rpcId, serverRpc, writer.ToArray());
    }

    public virtual bool __AstraNet_InvokeServerRpc(uint rpcId, NetworkReader reader) => false;
    public virtual bool __AstraNet_InvokeClientRpc(uint rpcId, NetworkReader reader) => false;
    public virtual void __AstraNet_WriteState(NetworkWriter writer) { }
    public virtual void __AstraNet_ReadState(NetworkReader reader) => reader.EnsureEnd();
}
