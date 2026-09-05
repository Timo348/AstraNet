using System.Security.Cryptography;
using System.Reflection.PortableExecutable;
using AstraNet.Core;
using AstraNet.Weaver;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AstraNet.UnitTests;

[NetworkBehaviour]
public sealed class WovenProbe : NetworkBehaviourBase
{
    [SyncVar] public int Health = 100;
    [SyncVar] public string? Name = "probe";
    [SyncVar] public Coordinates Position;
    public int Calls;
    public int Finalizers;
    public int Effects;
    public WovenProbe? ForwardTarget;

    [ServerRpc]
    public void Damage(int amount)
    {
        int effective = Math.Max(amount, 0);
        try
        {
            if (effective > 1000) throw new ArgumentOutOfRangeException(nameof(amount));
            Health -= effective;
            Calls++;
        }
        catch (ArgumentOutOfRangeException) { Health = -1; }
        finally { Finalizers++; }
    }

    [ServerRpc]
    public void Damage(string reason) { Name = reason; Calls++; }

    [ServerRpc]
    public void Move(Coordinates position) { Position = position; }

    [ClientRpc]
    public void Effect(int count) { for (int i = 0; i < count; i++) Effects++; }

    [ClientRpc]
    public void RecursiveEffect(int count)
    {
        if (count <= 0) return;
        Effects++;
        RecursiveEffect(count - 1);
    }

    [ServerRpc]
    public void Forward(int value)
    {
        Calls++;
        if (ForwardTarget is not null) ForwardTarget.Forward(value - 1);
    }
}

public sealed class WeaverTests
{
    private sealed class ProbeContext(bool isServer) : INetworkContext
    {
        public bool IsServer => isServer;
        public readonly List<(uint Id, bool ServerRpc, byte[] Payload)> Sent = [];
        public void SendRpc(NetworkBehaviourBase behaviour, uint rpcId, bool serverRpc, byte[] payload)
            => Sent.Add((rpcId, serverRpc, payload));
    }

    private static uint Id(string name, string args) => AssemblyWeaver.ComputeId($"AstraNet.UnitTests.WovenProbe::{name}({args})");

    [Fact]
    public void BuildActuallyRewritesMethodsAndGeneratesStateAndSerialization()
    {
        var path = typeof(WovenProbe).Assembly.Location;
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        Assert.Contains(assembly.CustomAttributes, a => a.AttributeType.FullName == typeof(AstraNetWovenAttribute).FullName);
        var type = assembly.MainModule.GetType(typeof(WovenProbe).FullName);
        foreach (var rpc in type.Methods.Where(m => m.CustomAttributes.Any(a => a.AttributeType.Name is "ServerRpcAttribute" or "ClientRpcAttribute")))
        {
            Assert.Contains(rpc.Body.Instructions, i => i.Operand is MethodReference method && method.Name == "__AstraNet_SendRpc");
            var implementation = type.Methods.Single(m => m.Name == $"__AstraNet_{rpc.Name}_{AssemblyWeaver.ComputeId(AssemblyWeaver.RpcIdentity(rpc)):X8}_Impl");
            Assert.True(implementation.Body.Instructions.Count > 2);
        }
        Assert.Contains(type.Methods, m => m.Name == "__AstraNet_WriteState" && m.IsVirtual);
        Assert.Contains(type.Methods, m => m.Name == "__AstraNet_ReadState" && m.IsVirtual);
        Assert.Contains(type.Methods, m => m.Name == "__AstraNet_InvokeServerRpc");
        Assert.Contains(type.Methods, m => m.Name == "__AstraNet_InvokeClientRpc");
        Assert.Contains(assembly.MainModule.GetType(typeof(PlayerData).FullName).Methods, m => m.Name.StartsWith("__AstraNet_") && m.IsStatic);
        var moduleInitializer = assembly.MainModule.Types.Single(t => t.Name == "<Module>").Methods.Single(m => m.Name == ".cctor");
        Assert.Contains(moduleInitializer.Body.Instructions, i => i.OpCode == OpCodes.Stsfld);
    }

    [Fact]
    public void ServerWrappersRouteAndPreserveOriginalExceptionHandlersAndLocals()
    {
        var clientContext = new ProbeContext(false);
        var client = new WovenProbe();
        client.Attach(clientContext, 7, 3);
        client.Damage(10);
        Assert.Equal(100, client.Health);
        var packet = Assert.Single(clientContext.Sent);
        Assert.True(packet.ServerRpc);
        Assert.Equal(Id("Damage", "System.Int32"), packet.Id);
        var authoritative = new WovenProbe();
        authoritative.Attach(new ProbeContext(true), 7, 3);
        Assert.True(authoritative.__AstraNet_InvokeServerRpc(packet.Id, new NetworkReader(packet.Payload)));
        Assert.Equal(90, authoritative.Health);
        Assert.Equal(1, authoritative.Calls);
        Assert.Equal(1, authoritative.Finalizers);
        authoritative.Damage(2000); // local authoritative invocation takes the original body's catch/finally
        Assert.Equal(-1, authoritative.Health);
        Assert.Equal(2, authoritative.Finalizers);
        Assert.False(authoritative.__AstraNet_InvokeServerRpc(0, new NetworkReader([])));
    }

    [Fact]
    public void ClientRpcSendsWithoutRunningLocallyAndReceivingRunsExactlyOnce()
    {
        var context = new ProbeContext(true);
        var server = new WovenProbe();
        server.Attach(context, 1, 0);
        server.Effect(4);
        Assert.Equal(0, server.Effects);
        var packet = Assert.Single(context.Sent);
        Assert.False(packet.ServerRpc);
        var client = new WovenProbe();
        client.Attach(new ProbeContext(false), 1, 0);
        Assert.True(client.__AstraNet_InvokeClientRpc(packet.Id, new NetworkReader(packet.Payload)));
        Assert.Equal(4, client.Effects);
        Assert.Throws<InvalidOperationException>(() => client.Effect(1));
        Assert.Throws<InvalidOperationException>(() => new WovenProbe().Damage(1));
    }

    [Fact]
    public void RecursiveReceivedBodyStaysLocalAndExecutesWithoutReenteringSendWrapper()
    {
        var context = new ProbeContext(true);
        var server = new WovenProbe();
        server.Attach(context, 1, 0);
        server.RecursiveEffect(4);
        var packet = Assert.Single(context.Sent);
        var clientContext = new ProbeContext(false);
        var client = new WovenProbe();
        client.Attach(clientContext, 1, 0);
        Assert.True(client.__AstraNet_InvokeClientRpc(packet.Id, new NetworkReader(packet.Payload)));
        Assert.Equal(4, client.Effects);
        Assert.Empty(clientContext.Sent);
    }

    [Fact]
    public void SameMethodOnAnotherInstanceRetainsItsNetworkRouting()
    {
        var remoteContext = new ProbeContext(false);
        var remote = new WovenProbe();
        remote.Attach(remoteContext, 2, 0);
        var authoritative = new WovenProbe { ForwardTarget = remote };
        authoritative.Attach(new ProbeContext(true), 1, 0);
        authoritative.Forward(10);
        Assert.Equal(1, authoritative.Calls);
        Assert.Equal(0, remote.Calls);
        var sent = Assert.Single(remoteContext.Sent);
        Assert.Equal(Id("Forward", "System.Int32"), sent.Id);
        Assert.Equal(9, new NetworkReader(sent.Payload).ReadInt32());
    }

    [Fact]
    public void OverloadsHaveDistinctStableIdsAndStructArgumentsExecute()
    {
        var context = new ProbeContext(false);
        var client = new WovenProbe();
        client.Attach(context, 1, 0);
        client.Damage(1);
        client.Damage("different overload");
        var position = new Coordinates { X = 2.5f, Y = 44.0, Z = -7 };
        client.Move(position);
        Assert.NotEqual(context.Sent[0].Id, context.Sent[1].Id);
        Assert.Equal(Id("Damage", "System.String"), context.Sent[1].Id);
        var server = new WovenProbe();
        foreach (var packet in context.Sent)
            Assert.True(server.__AstraNet_InvokeServerRpc(packet.Id, new NetworkReader(packet.Payload)));
        Assert.Equal(99, server.Health);
        Assert.Equal("different overload", server.Name);
        Assert.Equal(position, server.Position);
    }

    [Fact]
    public void StateAndRpcReadersValidateEntirePayloadBeforeMutating()
    {
        var server = new WovenProbe { Health = 90, Name = "replicated", Position = new Coordinates { X = 9, Y = 8, Z = 7 } };
        var writer = new NetworkWriter();
        server.__AstraNet_WriteState(writer);
        var client = new WovenProbe();
        byte[] valid = writer.ToArray();
        foreach (var invalid in new[] { valid[..^1], valid.Concat(new byte[] { 0 }).ToArray() })
        {
            Assert.Throws<NetworkProtocolException>(() => client.__AstraNet_ReadState(new NetworkReader(invalid)));
            Assert.Equal(100, client.Health);
            Assert.Equal("probe", client.Name);
        }
        client.__AstraNet_ReadState(new NetworkReader(valid));
        Assert.Equal(90, client.Health);
        Assert.Equal(server.Name, client.Name);
        Assert.Equal(server.Position, client.Position);
        var argument = new NetworkWriter();
        argument.WriteInt32(20);
        argument.WriteByte(7);
        Assert.Throws<NetworkProtocolException>(() => client.__AstraNet_InvokeServerRpc(Id("Damage", "System.Int32"), new NetworkReader(argument.ToArray())));
        Assert.Equal(90, client.Health);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public void ReweavingIsIdempotentAndDoesNotAlterAssemblyOrPdb()
    {
        using var temp = new TemporaryDirectory();
        string original = typeof(WovenProbe).Assembly.Location;
        string copy = Path.Combine(temp.Path, "Consumer.dll");
        File.Copy(original, copy);
        string pdb = Path.ChangeExtension(original, ".pdb");
        Assert.True(File.Exists(pdb));
        File.Copy(pdb, Path.ChangeExtension(copy, ".pdb"));
        byte[] before = SHA256.HashData(File.ReadAllBytes(copy));
        byte[] symbolsBefore = SHA256.HashData(File.ReadAllBytes(Path.ChangeExtension(copy, ".pdb")));
        var result = AssemblyWeaver.Weave(copy, [System.IO.Path.GetDirectoryName(original)!]);
        Assert.False(result.Modified);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(copy)));
        Assert.Equal(symbolsBefore, SHA256.HashData(File.ReadAllBytes(Path.ChangeExtension(copy, ".pdb"))));
    }

    [Fact]
    public void PortableSymbolsFollowOriginalBodyAndDoNotReferenceStagingDirectory()
    {
        string path = typeof(WovenProbe).Assembly.Location;
        using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadSymbols = true });
        var type = assembly.MainModule.GetType(typeof(WovenProbe).FullName);
        var implementation = type.Methods.Single(m => m.Name == $"__AstraNet_Damage_{Id("Damage", "System.Int32"):X8}_Impl");
        Assert.NotEmpty(implementation.Body.ExceptionHandlers);
        Assert.NotEmpty(implementation.DebugInformation.SequencePoints);
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var codeView = pe.ReadCodeViewDebugDirectoryData(pe.ReadDebugDirectory().Single(e => e.Type == DebugDirectoryEntryType.CodeView));
        Assert.DoesNotContain(".astranet-", codeView.Path);
        Assert.EndsWith("AstraNet.UnitTests.pdb", codeView.Path);
    }

    [Fact]
    public void MismatchedWovenVersionRequiresRebuild()
    {
        using var temp = new TemporaryDirectory();
        string path = System.IO.Path.Combine(temp.Path, "OldVersion.dll");
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("OldVersion", new Version(1, 0)), "OldVersion", ModuleKind.Dll))
        {
            var marker = new CustomAttribute(assembly.MainModule.ImportReference(typeof(AstraNetWovenAttribute).GetConstructor([typeof(string)])!));
            marker.ConstructorArguments.Add(new CustomAttributeArgument(assembly.MainModule.TypeSystem.String, "0.0.0"));
            assembly.CustomAttributes.Add(marker);
            assembly.Write(path);
        }
        var error = Assert.Throws<WeavingException>(() => AssemblyWeaver.Weave(path));
        Assert.Contains("0.0.0", error.Message);
    }

    [Theory]
    [InlineData("static_rpc")]
    [InlineData("return_rpc")]
    [InlineData("generic_rpc")]
    [InlineData("byref_rpc")]
    [InlineData("readonly_sync")]
    [InlineData("unsupported_sync")]
    [InlineData("missing_behaviour_attribute")]
    public void UnsupportedShapesFailBeforeChangingInputAssembly(string kind)
    {
        using var temp = new TemporaryDirectory();
        string path = System.IO.Path.Combine(temp.Path, "BadConsumer.dll");
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("BadConsumer", new Version(1, 0)), "BadConsumer", ModuleKind.Dll))
        {
            var module = assembly.MainModule;
            var type = new TypeDefinition("Fixture", "InvalidBehaviour", TypeAttributes.Public | TypeAttributes.Class, module.ImportReference(typeof(NetworkBehaviourBase)));
            module.Types.Add(type);
            if (kind != "missing_behaviour_attribute")
                type.CustomAttributes.Add(Attribute<NetworkBehaviourAttribute>(module));
            if (kind.EndsWith("sync"))
            {
                var field = new FieldDefinition("State", FieldAttributes.Public | (kind == "readonly_sync" ? FieldAttributes.InitOnly : 0),
                    kind == "unsupported_sync" ? module.ImportReference(typeof(DateTime)) : module.TypeSystem.Int32);
                field.CustomAttributes.Add(Attribute<SyncVarAttribute>(module));
                type.Fields.Add(field);
            }
            else
            {
                var method = new MethodDefinition("Invalid", MethodAttributes.Public | (kind == "static_rpc" ? MethodAttributes.Static : 0),
                    kind == "return_rpc" ? module.TypeSystem.Int32 : module.TypeSystem.Void);
                method.CustomAttributes.Add(Attribute<ServerRpcAttribute>(module));
                type.Methods.Add(method);
                if (kind == "generic_rpc") method.GenericParameters.Add(new GenericParameter("T", method));
                if (kind == "byref_rpc") method.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, new ByReferenceType(module.TypeSystem.Int32)));
                var il = method.Body.GetILProcessor();
                if (kind == "return_rpc") il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ret);
            }
            assembly.Write(path);
        }
        byte[] before = File.ReadAllBytes(path);
        var error = Assert.Throws<WeavingException>(() => AssemblyWeaver.Weave(path, [System.IO.Path.GetDirectoryName(typeof(NetworkBehaviourBase).Assembly.Location)!]));
        Assert.Contains("InvalidBehaviour", error.Message);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    private static CustomAttribute Attribute<T>(ModuleDefinition module) where T : System.Attribute =>
        new(module.ImportReference(typeof(T).GetConstructor(Type.EmptyTypes)!));

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "astranet-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
