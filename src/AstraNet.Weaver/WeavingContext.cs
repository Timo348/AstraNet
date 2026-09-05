using Mono.Cecil;

namespace AstraNet.Weaver;

internal sealed class WeavingContext
{
    public ModuleDefinition Module { get; }
    private readonly ModuleDefinition core;
    public TypeReference WriterType { get; }
    public TypeReference ReaderType { get; }
    public TypeDefinition SerializerDefinition { get; }

    public WeavingContext(ModuleDefinition module, IAssemblyResolver resolver)
    {
        Module = module;
        var reference = module.AssemblyReferences.FirstOrDefault(r => r.Name == "AstraNet.Core")
            ?? throw new WeavingException("Consumer uses networking attributes but has no reference to AstraNet.Core.");
        try { core = resolver.Resolve(reference).MainModule; }
        catch (AssemblyResolutionException ex)
        {
            throw new WeavingException($"Cannot resolve AstraNet.Core. Pass its build output directory or reference assembly path to the weaver. {ex.Message}");
        }
        WriterType = module.ImportReference(CoreType("NetworkWriter"));
        ReaderType = module.ImportReference(CoreType("NetworkReader"));
        SerializerDefinition = CoreType("NetworkSerializer`1");
    }

    public TypeDefinition CoreType(string name) => core.GetType("AstraNet.Core." + name)
        ?? throw new WeavingException($"Required core type AstraNet.Core.{name} was not found.");

    public MethodReference ImportCoreMethod(string type, string name, int parameterCount) => Module.ImportReference(
        CoreType(type).Methods.Single(m => m.Name == name && m.Parameters.Count == parameterCount));

    public GenericInstanceType SerializerType(TypeReference type)
    {
        var result = new GenericInstanceType(Module.ImportReference(SerializerDefinition));
        result.GenericArguments.Add(Module.ImportReference(type));
        return result;
    }

    public MethodReference SerializationMethod(TypeReference type, bool write)
    {
        var method = SerializerDefinition.Methods.Single(m => m.Name == (write ? "Write" : "Read"));
        return HostMethod(Module.ImportReference(method), SerializerType(type));
    }

    public static MethodReference HostMethod(MethodReference method, TypeReference host)
    {
        var reference = new MethodReference(method.Name, method.ReturnType, host)
        {
            HasThis = method.HasThis,
            ExplicitThis = method.ExplicitThis,
            CallingConvention = method.CallingConvention
        };
        foreach (var parameter in method.Parameters) reference.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
        foreach (var parameter in method.GenericParameters) reference.GenericParameters.Add(new GenericParameter(parameter.Name, reference));
        return reference;
    }
}
