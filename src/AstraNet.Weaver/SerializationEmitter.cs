using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AstraNet.Weaver;

internal sealed class SerializationEmitter(WeavingContext context)
{
    private static readonly HashSet<string> Primitives =
    [
        "System.Byte", "System.SByte", "System.Boolean", "System.Int16", "System.UInt16", "System.Int32", "System.UInt32",
        "System.Int64", "System.UInt64", "System.Single", "System.Double", "System.String"
    ];
    private readonly Dictionary<string, TypeDefinition> required = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (MethodDefinition Write, MethodDefinition Read)> generated = new(StringComparer.Ordinal);
    private TypeDefinition? enumHelpers;
    private ModuleDefinition Module => context.Module;

    public void Require(TypeReference reference, string member)
    {
        if (Primitives.Contains(reference.FullName)) return;
        if (reference is ArrayType { Rank: 1, IsVector: true } array && array.ElementType.FullName == "System.Byte") return;
        if (reference is TypeSpecification || reference.IsGenericParameter)
            throw Unsupported(reference, member, "Generic types, arrays other than byte[], by-reference types and pointers are unsupported.");
        TypeDefinition type;
        try { type = reference.Resolve() ?? throw new WeavingException($"Cannot resolve {reference.FullName}."); }
        catch (AssemblyResolutionException exception) { throw Unsupported(reference, member, exception.Message); }
        if (!type.IsValueType || type.HasGenericParameters)
            throw Unsupported(reference, member, "Only supported primitives, enums and non-generic value-type structs can be serialized.");
        if (required.ContainsKey(type.FullName)) return;
        if (!type.IsEnum && type.Module != Module)
            throw Unsupported(reference, member, "Custom structs must be declared in the consumer assembly being woven.");
        if (type.DeclaringType is not null && HasGenericContainer(type))
            throw Unsupported(reference, member, "Structs nested in generic types are unsupported.");
        if (!type.IsEnum && type.Methods.Any(m => m.Name.StartsWith(AssemblyWeaver.Prefix, StringComparison.Ordinal)))
            throw Unsupported(reference, member, "The __AstraNet_ method prefix is reserved for generated serializers.");
        required.Add(type.FullName, type);
        if (type.IsEnum)
        {
            Require(EnumUnderlyingType(type), member);
            return;
        }
        foreach (var field in SerializedFields(type))
        {
            if (field.IsInitOnly) throw AssemblyWeaver.Error(field, "Readonly struct fields cannot be deserialized. Use mutable fields.");
            Require(field.FieldType, field.FullName);
        }
    }

    public int Emit()
    {
        foreach (var type in required.Values.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            var methods = type.IsEnum ? EmitEnum(type) : EmitStruct(type);
            generated.Add(type.FullName, methods);
        }
        if (generated.Count > 0) EmitRegistration();
        return generated.Count;
    }

    private (MethodDefinition Write, MethodDefinition Read) EmitStruct(TypeDefinition type)
    {
        var (write, read) = NewMethods(type, type, "");
        var writeIl = write.Body.GetILProcessor();
        foreach (var field in SerializedFields(type))
        {
            writeIl.Emit(OpCodes.Ldarg_0);
            writeIl.Emit(OpCodes.Ldarga, write.Parameters[1]);
            writeIl.Emit(OpCodes.Ldfld, field);
            writeIl.Emit(OpCodes.Call, context.SerializationMethod(field.FieldType, write: true));
        }
        writeIl.Emit(OpCodes.Ret);
        var readIl = read.Body.GetILProcessor();
        var result = new VariableDefinition(type);
        read.Body.Variables.Add(result);
        read.Body.InitLocals = true;
        readIl.Emit(OpCodes.Ldloca, result);
        readIl.Emit(OpCodes.Initobj, type);
        foreach (var field in SerializedFields(type))
        {
            readIl.Emit(OpCodes.Ldloca, result);
            readIl.Emit(OpCodes.Ldarg_0);
            readIl.Emit(OpCodes.Call, context.SerializationMethod(field.FieldType, write: false));
            readIl.Emit(OpCodes.Stfld, field);
        }
        readIl.Emit(OpCodes.Ldloc, result);
        readIl.Emit(OpCodes.Ret);
        return (write, read);
    }

    private (MethodDefinition Write, MethodDefinition Read) EmitEnum(TypeDefinition type)
    {
        if (enumHelpers is null)
        {
            if (Module.GetType("AstraNet.Generated.__AstraNet_Serializers") is not null)
                throw new WeavingException("The type AstraNet.Generated.__AstraNet_Serializers is reserved for generated enum serializers.");
            enumHelpers = new TypeDefinition("AstraNet.Generated", "__AstraNet_Serializers",
                TypeAttributes.Class | TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
                Module.TypeSystem.Object);
            Module.Types.Add(enumHelpers);
        }
        var suffix = "_" + AssemblyWeaver.ComputeId(type.FullName).ToString("X8");
        var (write, read) = NewMethods(enumHelpers, Module.ImportReference(type), suffix);
        var underlying = EnumUnderlyingType(type);
        var writeIl = write.Body.GetILProcessor();
        writeIl.Emit(OpCodes.Ldarg_0);
        writeIl.Emit(OpCodes.Ldarg_1);
        writeIl.Emit(OpCodes.Call, context.SerializationMethod(underlying, write: true));
        writeIl.Emit(OpCodes.Ret);
        var readIl = read.Body.GetILProcessor();
        readIl.Emit(OpCodes.Ldarg_0);
        readIl.Emit(OpCodes.Call, context.SerializationMethod(underlying, write: false));
        readIl.Emit(OpCodes.Ret);
        return (write, read);
    }

    private (MethodDefinition Write, MethodDefinition Read) NewMethods(TypeDefinition host, TypeReference valueType, string suffix)
    {
        var write = new MethodDefinition("__AstraNet_Serialize" + suffix,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, Module.TypeSystem.Void);
        write.Parameters.Add(new ParameterDefinition("writer", ParameterAttributes.None, context.WriterType));
        write.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, valueType));
        var read = new MethodDefinition("__AstraNet_Deserialize" + suffix,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, valueType);
        read.Parameters.Add(new ParameterDefinition("reader", ParameterAttributes.None, context.ReaderType));
        host.Methods.Add(write);
        host.Methods.Add(read);
        return (write, read);
    }

    private void EmitRegistration()
    {
        var moduleType = Module.Types.Single(t => t.Name == "<Module>");
        var initializer = moduleType.Methods.SingleOrDefault(m => m.Name == ".cctor");
        if (initializer is null)
        {
            initializer = new MethodDefinition(".cctor",
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                Module.TypeSystem.Void);
            moduleType.Methods.Add(initializer);
            initializer.Body.GetILProcessor().Emit(OpCodes.Ret);
        }
        var originalFirst = initializer.Body.Instructions[0];
        var il = initializer.Body.GetILProcessor();
        var bootstraps = new Dictionary<TypeDefinition, MethodDefinition>();
        foreach (var type in required.Values.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            var methods = generated[type.FullName];
            var destination = type.Module == Module && type.DeclaringType is not null
                ? Bootstrap(type.DeclaringType) : initializer;
            var destinationIl = destination.Body.GetILProcessor();
            var before = destination == initializer ? originalFirst : destination.Body.Instructions[^1];
            Register("Writer", typeof(Action<,>), [context.WriterType, Module.ImportReference(type)], methods.Write);
            Register("Reader", typeof(Func<,>), [context.ReaderType, Module.ImportReference(type)], methods.Read);

            void Register(string fieldName, Type delegateDefinition, TypeReference[] arguments, MethodDefinition method)
            {
                var delegateType = new GenericInstanceType(Module.ImportReference(delegateDefinition));
                foreach (var argument in arguments) delegateType.GenericArguments.Add(argument);
                var constructor = WeavingContext.HostMethod(Module.ImportReference(delegateDefinition.GetConstructors()[0]), delegateType);
                var fieldDefinition = context.SerializerDefinition.Fields.Single(f => f.Name == fieldName);
                // Import the field as a member first, so Cecil has the owning !0 generic context.
                var field = new FieldReference(fieldName, Module.ImportReference(fieldDefinition).FieldType, context.SerializerType(type));
                destinationIl.InsertBefore(before, Instruction.Create(OpCodes.Ldnull));
                destinationIl.InsertBefore(before, Instruction.Create(OpCodes.Ldftn, method));
                destinationIl.InsertBefore(before, Instruction.Create(OpCodes.Newobj, constructor));
                destinationIl.InsertBefore(before, Instruction.Create(OpCodes.Stsfld, field));
            }
        }
        initializer.Body.MaxStackSize = Math.Max(initializer.Body.MaxStackSize, 3);

        MethodDefinition Bootstrap(TypeDefinition container)
        {
            if (bootstraps.TryGetValue(container, out var existing)) return existing;
            const string name = "__AstraNet_SerializerBootstrap";
            if (container.NestedTypes.Any(t => t.Name == name))
                throw AssemblyWeaver.Error(container, $"Nested type name '{name}' is reserved for generated serializer registration.");
            // A generated nested helper avoids running any user-defined static constructor on the container.
            var helper = new TypeDefinition("", name, TypeAttributes.NestedAssembly | TypeAttributes.Class |
                TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, Module.TypeSystem.Object);
            container.NestedTypes.Add(helper);
            var register = new MethodDefinition("Register", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                Module.TypeSystem.Void);
            register.Body.MaxStackSize = 3;
            register.Body.GetILProcessor().Emit(OpCodes.Ret);
            helper.Methods.Add(register);
            bootstraps.Add(container, register);
            var parent = container.DeclaringType is null ? initializer : Bootstrap(container.DeclaringType);
            parent.Body.GetILProcessor().InsertBefore(parent == initializer ? originalFirst : parent.Body.Instructions[^1],
                Instruction.Create(OpCodes.Call, register));
            return register;
        }
    }

    private static IEnumerable<FieldDefinition> SerializedFields(TypeDefinition type) => type.Fields.Where(f => !f.IsStatic);
    private static TypeReference EnumUnderlyingType(TypeDefinition type) => type.Fields.Single(f => f.Name == "value__").FieldType;
    private static bool HasGenericContainer(TypeDefinition type) => type.HasGenericParameters ||
        (type.DeclaringType is not null && HasGenericContainer(type.DeclaringType));
    private static WeavingException Unsupported(TypeReference type, string member, string detail) =>
        new($"{member}: Cannot serialize '{type.FullName}'. {detail}");
}
