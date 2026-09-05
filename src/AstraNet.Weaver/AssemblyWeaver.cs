using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AstraNet.Weaver;

public sealed record WeaveResult(bool Modified, int BehaviourCount, int RpcCount, int SerializerCount);

public sealed class WeavingException(string message) : Exception(message);

/// <summary>Rewrites a compiled consumer assembly. The input is only replaced after successful generation.</summary>
public static class AssemblyWeaver
{
    public const string Version = "1.0.0";
    internal const string Prefix = "__AstraNet_";

    public static WeaveResult Weave(string assemblyPath, IEnumerable<string>? referenceDirectories = null)
    {
        assemblyPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(assemblyPath)) throw new WeavingException($"Assembly does not exist: {assemblyPath}");
        using var resolver = new DefaultAssemblyResolver();
        var searchPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetDirectoryName(assemblyPath)!, AppContext.BaseDirectory,
            Path.GetDirectoryName(typeof(object).Assembly.Location)!
        };
        foreach (var reference in referenceDirectories ?? [])
        {
            var path = reference.Trim().Trim('"');
            if (File.Exists(path)) path = Path.GetDirectoryName(Path.GetFullPath(path))!;
            if (Directory.Exists(path)) searchPaths.Add(Path.GetFullPath(path));
        }
        foreach (var path in searchPaths) resolver.AddSearchDirectory(path);

        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        var symbols = File.Exists(pdbPath);
        using var assemblyBytes = new MemoryStream(File.ReadAllBytes(assemblyPath));
        using var symbolBytes = symbols ? new MemoryStream(File.ReadAllBytes(pdbPath)) : null;
        using var assembly = AssemblyDefinition.ReadAssembly(assemblyBytes, new ReaderParameters
        {
            AssemblyResolver = resolver,
            ReadingMode = ReadingMode.Immediate,
            InMemory = true,
            ReadSymbols = symbols,
            SymbolStream = symbolBytes,
            SymbolReaderProvider = symbols ? new PortablePdbReaderProvider() : null
        });

        var markers = assembly.CustomAttributes.Where(a => a.AttributeType.FullName == "AstraNet.Core.AstraNetWovenAttribute").ToArray();
        if (markers.Length > 0)
        {
            var recordedVersion = markers.Length == 1 && markers[0].ConstructorArguments.Count == 1
                ? markers[0].ConstructorArguments[0].Value as string : null;
            if (recordedVersion != Version)
                throw new WeavingException($"Assembly has an incompatible AstraNet weaving marker '{recordedVersion ?? "invalid"}'; this weaver is {Version}. Clean and rebuild the consumer from source.");
            return new WeaveResult(false, 0, 0, 0);
        }
        if (assembly.Name.HasPublicKey)
            throw new WeavingException("Signed consumer assemblies are unsupported: weaving would invalidate the strong-name signature.");

        var types = AllTypes(assembly.MainModule.Types).ToArray();
        var behaviours = types.Where(t => HasAttribute(t, "NetworkBehaviourAttribute")).ToArray();
        var serializable = types.Where(t => HasAttribute(t, "NetworkSerializableAttribute") || HasAttribute(t, "NetworkMessageAttribute")).ToArray();
        foreach (var type in types)
        {
            if (behaviours.Contains(type)) continue;
            foreach (var method in type.Methods.Where(m => HasAttribute(m, "ServerRpcAttribute") || HasAttribute(m, "ClientRpcAttribute")))
                throw Error(method, "RPC methods require a class marked [NetworkBehaviour].");
            foreach (var field in type.Fields.Where(f => HasAttribute(f, "SyncVarAttribute")))
                throw Error(field, "[SyncVar] fields require a class marked [NetworkBehaviour].");
        }
        if (behaviours.Length == 0 && serializable.Length == 0) return new WeaveResult(false, 0, 0, 0);

        var context = new WeavingContext(assembly.MainModule, resolver);
        var serializers = new SerializationEmitter(context);
        foreach (var type in serializable) serializers.Require(type, type.FullName);
        var emitters = behaviours.Select(t => new BehaviourEmitter(context, serializers, t)).ToArray();
        // Validate the whole module before mutating any consumer metadata or replacing files.
        foreach (var emitter in emitters) emitter.Validate();
        var generatedSerializerCount = serializers.Emit();
        foreach (var emitter in emitters) emitter.Emit();

        var marker = new CustomAttribute(context.ImportCoreMethod("AstraNetWovenAttribute", ".ctor", 1));
        marker.ConstructorArguments.Add(new CustomAttributeArgument(assembly.MainModule.TypeSystem.String, Version));
        assembly.CustomAttributes.Add(marker);

        var temporaryDirectory = Path.Combine(Path.GetDirectoryName(assemblyPath)!, ".astranet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var output = Path.Combine(temporaryDirectory, Path.GetFileName(assemblyPath));
            assembly.Write(output, new WriterParameters
            {
                WriteSymbols = symbols,
                SymbolWriterProvider = symbols ? new StablePortablePdbWriterProvider(Path.GetFileName(pdbPath)) : null
            });
            File.Move(output, assemblyPath, overwrite: true);
            if (symbols) File.Move(Path.ChangeExtension(output, ".pdb"), pdbPath, overwrite: true);
        }
        finally
        {
            // Only our unique staging directory and its known files can be removed here.
            foreach (var file in Directory.EnumerateFiles(temporaryDirectory)) File.Delete(file);
            Directory.Delete(temporaryDirectory);
        }
        return new WeaveResult(true, behaviours.Length, emitters.Sum(e => e.RpcCount), generatedSerializerCount);
    }

    /// <summary>FNV-1a over UTF-8; the identity includes the declaring type and every parameter type.</summary>
    public static uint ComputeId(string identity)
    {
        var hash = 2166136261u;
        foreach (var value in Encoding.UTF8.GetBytes(identity)) hash = unchecked((hash ^ value) * 16777619u);
        return hash;
    }

    public static string RpcIdentity(MethodDefinition method) =>
        $"{method.DeclaringType.FullName}::{method.Name}({string.Join(",", method.Parameters.Select(p => p.ParameterType.FullName))})";

    internal static bool HasAttribute(ICustomAttributeProvider member, string name) =>
        member.CustomAttributes.Any(a => a.AttributeType.FullName == "AstraNet.Core." + name);

    internal static WeavingException Error(MemberReference member, string detail) => new($"{member.FullName}: {detail}");

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> roots)
    {
        foreach (var type in roots)
        {
            yield return type;
            foreach (var nested in AllTypes(type.NestedTypes)) yield return nested;
        }
    }
}
