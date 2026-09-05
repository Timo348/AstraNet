namespace AstraNet.Weaver;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: dotnet AstraNet.Weaver.dll <assembly.dll> [reference-directory|reference.dll|@references.rsp ...]");
            return 2;
        }
        try
        {
            var references = args.Skip(1).SelectMany(a => a.StartsWith('@')
                ? File.ReadAllLines(a[1..]).Where(line => !string.IsNullOrWhiteSpace(line))
                : new[] { a });
            var result = AssemblyWeaver.Weave(args[0], references);
            Console.WriteLine(result.Modified
                ? $"AstraNet: woven {Path.GetFileName(args[0])}: {result.BehaviourCount} behaviours, {result.RpcCount} RPCs, {result.SerializerCount} serializers."
                : $"AstraNet: {Path.GetFileName(args[0])} unchanged (already woven or no networking members).");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{args[0]} : error ASTRANET001: {exception.Message}");
            if (Environment.GetEnvironmentVariable("ASTRANET_WEAVER_TRACE") == "1") Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
