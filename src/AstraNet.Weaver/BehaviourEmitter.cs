using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AstraNet.Weaver;

internal sealed class BehaviourEmitter(WeavingContext context, SerializationEmitter serializers, TypeDefinition type)
{
    private sealed record Rpc(MethodDefinition Method, bool ServerRpc, uint Id)
    {
        public MethodDefinition Implementation { get; set; } = null!;
    }
    private readonly List<Rpc> rpcs = [];
    private FieldDefinition[] fields = [];
    private ModuleDefinition Module => context.Module;
    public int RpcCount => rpcs.Count;

    public void Validate()
    {
        if (!type.IsClass || type.IsValueType || type.BaseType?.FullName != "AstraNet.Core.NetworkBehaviourBase")
            throw AssemblyWeaver.Error(type, "[NetworkBehaviour] requires a class deriving directly from NetworkBehaviourBase; behaviour inheritance is unsupported.");
        for (var current = type; current is not null; current = current.DeclaringType)
            if (current.HasGenericParameters) throw AssemblyWeaver.Error(type, "Generic network behaviours and behaviours nested in generic types are unsupported.");
        if (type.Methods.Any(m => m.Name.StartsWith(AssemblyWeaver.Prefix, StringComparison.Ordinal)))
            throw AssemblyWeaver.Error(type, "The __AstraNet_ method prefix is reserved for generated networking code.");
        var ids = new Dictionary<uint, string>();
        foreach (var method in type.Methods)
        {
            var server = AssemblyWeaver.HasAttribute(method, "ServerRpcAttribute");
            var client = AssemblyWeaver.HasAttribute(method, "ClientRpcAttribute");
            if (!server && !client) continue;
            if (server && client) throw AssemblyWeaver.Error(method, "An RPC cannot have both [ServerRpc] and [ClientRpc].");
            if (method.IsStatic || method.IsVirtual || method.HasGenericParameters || !method.HasBody || method.ReturnType.FullName != "System.Void")
                throw AssemblyWeaver.Error(method, "RPCs must be non-static, non-virtual, non-generic instance methods with a void return type and a managed body.");
            if (method.IsConstructor || method.IsPInvokeImpl || method.CallingConvention == MethodCallingConvention.VarArg)
                throw AssemblyWeaver.Error(method, "Constructors, P/Invoke methods and vararg methods cannot be RPCs.");
            if (method.CustomAttributes.Any(a => a.AttributeType.FullName is "System.Runtime.CompilerServices.AsyncStateMachineAttribute"
                or "System.Runtime.CompilerServices.IteratorStateMachineAttribute" or "System.Runtime.CompilerServices.AsyncIteratorStateMachineAttribute"))
                throw AssemblyWeaver.Error(method, "Async and iterator RPCs are unsupported.");
            foreach (var parameter in method.Parameters)
            {
                if (parameter.ParameterType.IsByReference || parameter.IsOut)
                    throw AssemblyWeaver.Error(method, $"RPC parameter '{parameter.Name}' cannot be ref, in or out.");
                serializers.Require(parameter.ParameterType, method.FullName + " parameter " + parameter.Name);
            }
            var identity = AssemblyWeaver.RpcIdentity(method);
            var id = AssemblyWeaver.ComputeId(identity);
            if (ids.TryGetValue(id, out var previous))
                throw AssemblyWeaver.Error(method, $"RPC identifier collision 0x{id:X8} between '{previous}' and '{identity}'. Rename one method.");
            ids.Add(id, identity);
            rpcs.Add(new Rpc(method, server, id));
        }
        fields = type.Fields.Where(f => AssemblyWeaver.HasAttribute(f, "SyncVarAttribute")).ToArray();
        foreach (var field in fields)
        {
            if (field.IsStatic || field.IsInitOnly || field.IsLiteral)
                throw AssemblyWeaver.Error(field, "[SyncVar] requires a mutable instance field; static, const and readonly fields are unsupported.");
            serializers.Require(field.FieldType, field.FullName);
        }
    }

    public void Emit()
    {
        foreach (var rpc in rpcs)
        {
            rpc.Implementation = MoveImplementation(rpc);
            EmitWrapper(rpc);
        }
        EmitDispatch(serverRpc: true);
        EmitDispatch(serverRpc: false);
        EmitState();
    }

    private MethodDefinition MoveImplementation(Rpc rpc)
    {
        var method = rpc.Method;
        var implementation = new MethodDefinition($"__AstraNet_{method.Name}_{rpc.Id:X8}_Impl",
            MethodAttributes.Private | MethodAttributes.HideBySig, method.ReturnType)
        {
            ImplAttributes = method.ImplAttributes
        };
        foreach (var parameter in method.Parameters)
            implementation.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType));
        type.Methods.Add(implementation);

        var oldBody = method.Body;
        var body = new MethodBody(implementation) { InitLocals = oldBody.InitLocals, MaxStackSize = oldBody.MaxStackSize };
        implementation.Body = body;
        foreach (var variable in oldBody.Variables) body.Variables.Add(variable);
        var originalInstructions = oldBody.Instructions.ToArray();
        var boundaries = new HashSet<Instruction>();
        foreach (var instruction in originalInstructions)
        {
            if (instruction.Operand is Instruction target) boundaries.Add(target);
            else if (instruction.Operand is Instruction[] targets)
                foreach (var branch in targets) boundaries.Add(branch);
        }
        foreach (var handler in oldBody.ExceptionHandlers)
        {
            if (handler.TryStart is not null) boundaries.Add(handler.TryStart);
            if (handler.HandlerStart is not null) boundaries.Add(handler.HandlerStart);
            if (handler.FilterStart is not null) boundaries.Add(handler.FilterStart);
        }
        for (var index = 0; index < originalInstructions.Length; index++)
        {
            var instruction = originalInstructions[index];
            if (instruction.Operand is ParameterDefinition parameter)
                instruction.Operand = parameter.Index >= 0 ? implementation.Parameters[parameter.Index] : body.ThisParameter;
            else if (instruction.Operand is MethodReference called && called.FullName == method.FullName &&
                IsDirectSelfCall(originalInstructions, index, called, boundaries))
                instruction.Operand = implementation;
            body.Instructions.Add(instruction);
        }
        foreach (var handler in oldBody.ExceptionHandlers) body.ExceptionHandlers.Add(handler);

        // Keep source locations, lexical scopes, local variables and exception regions on the original logic.
        foreach (var point in method.DebugInformation.SequencePoints) implementation.DebugInformation.SequencePoints.Add(point);
        method.DebugInformation.SequencePoints.Clear();
        implementation.DebugInformation.Scope = method.DebugInformation.Scope;
        method.DebugInformation.Scope = null;
        foreach (var information in method.DebugInformation.CustomDebugInformations)
            implementation.DebugInformation.CustomDebugInformations.Add(information);
        method.DebugInformation.CustomDebugInformations.Clear();
        method.Body = new MethodBody(method) { InitLocals = true };
        return implementation;
    }

    private static bool IsDirectSelfCall(Instruction[] instructions, int callIndex, MethodReference called, HashSet<Instruction> boundaries)
    {
        // Trace the receiver's stack producer within one basic block. A call on another object must
        // retain its network wrapper; matching the method signature alone would bypass that object's routing.
        if (instructions[callIndex].OpCode.Code is not (Code.Call or Code.Callvirt)) return false;
        var depth = called.Parameters.Count;
        for (var index = callIndex - 1; index >= 0; index--)
        {
            var instruction = instructions[index];
            if (instruction.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Return or FlowControl.Throw)
                return false;
            if (!StackEffect(instruction, out var pop, out var push)) return false;
            if (push > depth)
                return push == 1 && (instruction.OpCode.Code == Code.Ldarg_0 ||
                    (instruction.OpCode.Code is Code.Ldarg or Code.Ldarg_S && instruction.Operand is ParameterDefinition { Index: -1 }));
            depth += pop - push;
            if (boundaries.Contains(instruction)) return false;
        }
        return false;
    }

    private static bool StackEffect(Instruction instruction, out int pop, out int push)
    {
        pop = instruction.OpCode.StackBehaviourPop switch
        {
            StackBehaviour.Pop0 => 0,
            StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
            StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8 or
                StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
            StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8 or
                StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3,
            StackBehaviour.Varpop when instruction.Operand is MethodReference method =>
                method.Parameters.Count + (method.HasThis && instruction.OpCode.Code != Code.Newobj ? 1 : 0),
            _ => -1
        };
        push = instruction.OpCode.StackBehaviourPush switch
        {
            StackBehaviour.Push0 => 0,
            StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
            StackBehaviour.Varpush when instruction.Operand is MethodReference method => method.ReturnType.FullName == "System.Void" ? 0 : 1,
            _ => -1
        };
        return pop >= 0 && push >= 0;
    }

    private void EmitWrapper(Rpc rpc)
    {
        var method = rpc.Method;
        var il = method.Body.GetILProcessor();
        var send = Instruction.Create(OpCodes.Nop);
        var end = Instruction.Create(OpCodes.Ret);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(rpc.ServerRpc ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Call, context.ImportCoreMethod("NetworkBehaviourBase", "__AstraNet_ShouldSend", 1));
        il.Emit(OpCodes.Brtrue, send);
        if (rpc.ServerRpc)
        {
            il.Emit(OpCodes.Ldarg_0);
            foreach (var parameter in method.Parameters) il.Emit(OpCodes.Ldarg, parameter);
            il.Emit(OpCodes.Call, rpc.Implementation);
        }
        il.Emit(OpCodes.Br, end);
        il.Append(send);
        var writer = new VariableDefinition(context.WriterType);
        method.Body.Variables.Add(writer);
        il.Emit(OpCodes.Newobj, context.ImportCoreMethod("NetworkWriter", ".ctor", 0));
        il.Emit(OpCodes.Stloc, writer);
        foreach (var parameter in method.Parameters)
        {
            il.Emit(OpCodes.Ldloc, writer);
            il.Emit(OpCodes.Ldarg, parameter);
            il.Emit(OpCodes.Call, context.SerializationMethod(parameter.ParameterType, write: true));
        }
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)rpc.Id));
        il.Emit(rpc.ServerRpc ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, writer);
        il.Emit(OpCodes.Call, context.ImportCoreMethod("NetworkBehaviourBase", "__AstraNet_SendRpc", 3));
        il.Append(end);
    }

    private void EmitDispatch(bool serverRpc)
    {
        var method = NewOverride(serverRpc ? "__AstraNet_InvokeServerRpc" : "__AstraNet_InvokeClientRpc", Module.TypeSystem.Boolean,
            ("rpcId", Module.TypeSystem.UInt32), ("reader", context.ReaderType));
        var il = method.Body.GetILProcessor();
        foreach (var rpc in rpcs.Where(r => r.ServerRpc == serverRpc).OrderBy(r => r.Id))
        {
            var next = Instruction.Create(OpCodes.Nop);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldc_I4, unchecked((int)rpc.Id));
            il.Emit(OpCodes.Bne_Un, next);
            var arguments = new List<VariableDefinition>();
            foreach (var parameter in rpc.Method.Parameters)
            {
                var value = new VariableDefinition(parameter.ParameterType);
                method.Body.Variables.Add(value);
                arguments.Add(value);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Call, context.SerializationMethod(parameter.ParameterType, write: false));
                il.Emit(OpCodes.Stloc, value);
            }
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Callvirt, context.ImportCoreMethod("NetworkReader", "EnsureEnd", 0));
            il.Emit(OpCodes.Ldarg_0);
            foreach (var argument in arguments) il.Emit(OpCodes.Ldloc, argument);
            il.Emit(OpCodes.Call, rpc.Implementation);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ret);
            il.Append(next);
        }
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
    }

    private void EmitState()
    {
        var write = NewOverride("__AstraNet_WriteState", Module.TypeSystem.Void, ("writer", context.WriterType));
        var il = write.Body.GetILProcessor();
        foreach (var field in fields)
        {
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Call, context.SerializationMethod(field.FieldType, write: true));
        }
        il.Emit(OpCodes.Ret);

        var read = NewOverride("__AstraNet_ReadState", Module.TypeSystem.Void, ("reader", context.ReaderType));
        il = read.Body.GetILProcessor();
        var values = new List<VariableDefinition>();
        foreach (var field in fields)
        {
            var value = new VariableDefinition(field.FieldType);
            read.Body.Variables.Add(value);
            values.Add(value);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, context.SerializationMethod(field.FieldType, write: false));
            il.Emit(OpCodes.Stloc, value);
        }
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, context.ImportCoreMethod("NetworkReader", "EnsureEnd", 0));
        for (var index = 0; index < fields.Length; index++)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldloc, values[index]);
            il.Emit(OpCodes.Stfld, fields[index]);
        }
        il.Emit(OpCodes.Ret);
    }

    private MethodDefinition NewOverride(string name, TypeReference returnType, params (string Name, TypeReference Type)[] parameters)
    {
        var method = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig, returnType);
        foreach (var parameter in parameters)
            method.Parameters.Add(new ParameterDefinition(parameter.Name, ParameterAttributes.None, parameter.Type));
        method.Overrides.Add(context.ImportCoreMethod("NetworkBehaviourBase", name, parameters.Length));
        method.Body.InitLocals = true;
        type.Methods.Add(method);
        return method;
    }
}
