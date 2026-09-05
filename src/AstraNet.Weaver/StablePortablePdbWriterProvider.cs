using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AstraNet.Weaver;

/// <summary>Keep staging paths out of CodeView records; debuggers resolve the adjacent portable PDB.</summary>
internal sealed class StablePortablePdbWriterProvider(string pdbFileName) : ISymbolWriterProvider
{
    private readonly PortablePdbWriterProvider inner = new();
    public ISymbolWriter GetSymbolWriter(ModuleDefinition module, string fileName) =>
        new StableWriter(inner.GetSymbolWriter(module, fileName), pdbFileName);
    public ISymbolWriter GetSymbolWriter(ModuleDefinition module, Stream symbolStream) =>
        new StableWriter(inner.GetSymbolWriter(module, symbolStream), pdbFileName);

    private sealed class StableWriter(ISymbolWriter inner, string pdbFileName) : ISymbolWriter
    {
        public ISymbolReaderProvider GetReaderProvider() => inner.GetReaderProvider();
        public void Write(MethodDebugInformation information) => inner.Write(information);
        public void Write(ICustomDebugInformationProvider information) => inner.Write(information);
        public void Write() => inner.Write();
        public void Dispose() => inner.Dispose();

        public ImageDebugHeader GetDebugHeader()
        {
            var header = inner.GetDebugHeader();
            return new ImageDebugHeader(header.Entries.Select(entry =>
            {
                if (entry.Directory.Type != ImageDebugType.CodeView || entry.Data.Length < 24) return entry;
                // RSDS signature (4), PDB identity (16), age (4), then a null-terminated UTF-8 path.
                var path = Encoding.UTF8.GetBytes(pdbFileName + "\0");
                var data = new byte[24 + path.Length];
                Array.Copy(entry.Data, data, 24);
                path.CopyTo(data, 24);
                var directory = entry.Directory;
                directory.SizeOfData = data.Length;
                return new ImageDebugHeaderEntry(directory, data);
            }).ToArray());
        }
    }
}
