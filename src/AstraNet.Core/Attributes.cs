namespace AstraNet.Core;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NetworkBehaviourAttribute : Attribute;

[AttributeUsage(AttributeTargets.Field)]
public sealed class SyncVarAttribute : Attribute;

[AttributeUsage(AttributeTargets.Method)]
public sealed class ServerRpcAttribute : Attribute;

[AttributeUsage(AttributeTargets.Method)]
public sealed class ClientRpcAttribute : Attribute;

[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Enum)]
public sealed class NetworkSerializableAttribute : Attribute;

[AttributeUsage(AttributeTargets.Struct)]
public sealed class NetworkMessageAttribute : Attribute;

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class AstraNetWovenAttribute(string version) : Attribute
{
    public string Version { get; } = version;
}
