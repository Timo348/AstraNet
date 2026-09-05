# AstraNet

A small .NET 8 multiplayer networking framework with TCP and an opt-in reliable-UDP transport, plus a real Mono.Cecil build-time IL weaver. Networking behaviours, RPC dispatch, and struct serializers are generated into consumer assemblies. The runtime does not use reflection to serialize messages or invoke RPC methods.

## Build and run

Install the .NET 8 SDK. Package restore needs access to NuGet on the first build. No database, Docker, Unity, or external server is required.

```sh
dotnet build
dotnet test
dotnet run --project examples/AstraNet.ExampleServer -- --demo
```

The finite demo starts a real loopback TCP server and two clients, invokes `Damage(15)` from Client A, replicates `Health = 85` to both clients, broadcasts a damage effect, checks delivery counts, and exits with an error if expectations are not met.

For separate processes, first start the server, then an observer, then the attacking client in separate terminals:

```sh
dotnet run --project examples/AstraNet.ExampleServer -- --port 7777
dotnet run --project examples/AstraNet.ExampleClient -- --port 7777 --damage 0 --seconds 15
dotnet run --project examples/AstraNet.ExampleClient -- --port 7777 --damage 15 --seconds 10
```

The server binds to loopback by default and runs until Ctrl+C. The client accepts `--host`, `--port`, `--damage`, and `--seconds`. The standalone server sends snapshots every 100 ms, including to newly connected clients.

## Architecture and project layout

```text
AstraNet.sln
src/
  AstraNet.Core/             Attributes, typed codecs, behaviour contract
  AstraNet.Transport/        BCL TCP sockets, UDP sessions, and reliability layer
  AstraNet.Runtime/          Server, client, object registry, protocol dispatch
  AstraNet.Weaver/           Mono.Cecil CLI and reusable AssemblyWeaver API
build/
  AstraNet.Weaver.targets    Automatic consumer build integration
tests/
  AstraNet.UnitTests/        Serialization, real TCP framing, IL inspection
  AstraNet.IntegrationTests/ Multiple clients, RPCs, replication, bad peers
examples/
  AstraNet.Example.Shared/   Woven PlayerState shared by both executables
  AstraNet.ExampleServer/    Standalone server and finite two-client demo
  AstraNet.ExampleClient/    Standalone client
```

Core has no external package dependency. Transport depends on Core; Runtime depends on Core and Transport. Weaver uses [Mono.Cecil 0.11.6](https://www.nuget.org/packages/Mono.Cecil/0.11.6) and is a build dependency, not a runtime networking dependency. Tests use xUnit, Microsoft.NET.Test.Sdk, and Cecil for metadata inspection.

`NetworkServer` owns connections and an `(objectId, behaviourId)` registry. `NetworkClient` has an equivalent local registry. Multiple behaviours can belong to one object. Register the same IDs and compatible behaviour types at every participating peer before connecting. Object ID zero is reserved; behaviour ID zero is valid. Duplicate registrations fail explicitly.

## Writing a behaviour

```csharp
using AstraNet.Core;

[NetworkBehaviour]
public sealed class PlayerState : NetworkBehaviourBase
{
    [SyncVar] public int Health = 100;
    [SyncVar] public string Name = "Player";

    [ServerRpc]
    public void Damage(int amount)
    {
        Health -= amount;
    }

    [ClientRpc]
    public void PlayDamageEffect(int amount)
    {
        Console.WriteLine($"Damage effect: {amount}");
    }
}
```

Create independent instances and register them using the public API:

```csharp
await using var server = new AstraNet.Runtime.NetworkServer();
await using var client = new AstraNet.Runtime.NetworkClient();
var authoritative = new PlayerState();
var replica = new PlayerState();
server.RegisterBehaviour(1, 0, authoritative);
client.RegisterBehaviour(1, 0, replica);
await server.StartAsync(System.Net.IPAddress.Loopback, 0); // choose a free port
await client.ConnectAsync("127.0.0.1", server.Port);
replica.Damage(10); // sends a request; server processes it asynchronously
// For this same-process example, wait for authoritative processing with a deadline.
// A standalone server normally sends snapshots from its own update loop instead.
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
while (true)
{
    lock (authoritative) { if (authoritative.Health == 90) break; }
    await Task.Delay(10, deadline.Token);
}
await server.ReplicateAsync(1);
```

The runtime keeps gameplay code independent of the wire transport. Use the default constructors for TCP, or select reliable UDP explicitly:

```csharp
await using var server = new NetworkServer(NetworkTransportKind.ReliableUdp);
await using var client = new NetworkClient(NetworkTransportKind.ReliableUdp);
await server.StartAsync(IPAddress.Loopback, 0);
await client.ConnectAsync("127.0.0.1", server.Port);
await client.SendAsync(100, message, DeliveryMode.ReliableOrdered);
await client.SendAsync(101, snapshot, DeliveryMode.Unreliable);
```

TCP maps both delivery modes to its ordered stream. Reliable UDP uses a 32-message in-flight window because the acknowledgement header carries one cumulative ACK plus 32 history bits. A full reliable window reports `NetworkBackpressureException`; callers can await earlier sends or batch work below that limit.

A completed RPC call means the frame was written, not that the remote body finished. The demo waits for the server's authoritative change before requesting its snapshot. A real application should replicate from its update loop or use its own application-level acknowledgement message.

## Messages and serialization

```csharp
[NetworkMessage]
public struct ChatMessage
{
    public int Sequence;
    public string Text;
}

// Configure handlers before connecting; IDs are an explicit application contract.
server.OnMessage<ChatMessage>(100, (connection, message) =>
    Console.WriteLine($"{connection.Id}: {message.Text}"));
client.OnMessage<ChatMessage>(100, message => Console.WriteLine(message.Text));
await client.SendAsync(100, new ChatMessage { Sequence = 1, Text = "Hello" });
await server.BroadcastAsync(100, new ChatMessage { Sequence = 2, Text = "Welcome" });
await server.SendAsync(client.ConnectionId, 100, new ChatMessage { Text = "Private reply" });
```

`NetworkWriter` and `NetworkReader` encode `byte`, `sbyte`, `bool`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `string`, and `byte[]`. Integers are little endian; floating point preserves the IEEE bit pattern. A boolean is exactly one byte, 0 or 1. Strings use strict UTF-8. Strings and byte arrays have a signed 32-bit byte count; `-1` means null, `0` means empty. Other negative lengths, truncation, invalid UTF-8, invalid boolean bytes, and trailing payload bytes are rejected.

Mark standalone custom structs/enums `[NetworkSerializable]`, or messages `[NetworkMessage]`. Structs and enums referenced by RPC arguments or SyncVars are discovered recursively without requiring their own attribute. Generated struct codecs process all instance fields in metadata declaration order, including private fields and property backing fields. There is no padding or runtime property getter invocation. Static fields are ignored. All enum underlying widths work; undefined numeric enum values remain representable and application code must validate semantic restrictions itself.

`NetworkSerializer<T>.Writer` and `.Reader` are typed delegates. Core registers its fixed primitive codecs at module initialization; the weaver inserts registrations for generated custom codecs before the consumer's existing module initialization code. Nested structs are recursively encoded through these typed calls. No field reflection, `MethodInfo.Invoke`, name-based remote dispatch, or JSON fallback is involved.

### Allocation findings

The original implementation was safe managed code, but every writer path created a writer and copied `ToArray()`, and every reader path created a reader. The benchmark therefore measured avoidable per-operation allocations; pointers were not the missing feature. The new writer can wrap caller-owned storage or rent an `ArrayPool<byte>` buffer, `Reset()` keeps that storage, and the reader can be reset over an existing `ReadOnlyMemory<byte>`. Primitive reads/writes and borrowed byte-array slices use `Span`/`Memory` and `BinaryPrimitives` without unsafe code. `ToArray()`, `ReadString()`, and owning `ReadBytes()` still allocate by contract because they return owned objects.

BenchmarkDotNet `ShortRun` results on Windows 11, .NET SDK 8.0.423 (all values are mean ns/op and allocated bytes/op):

| Operation | Original | Reusable/borrowed path |
| --- | ---: | ---: |
| Write integers | 22.380 ns / 376 B | 5.475 ns / 0 B |
| Write representative RPC | 49.048 ns / 416 B | 26.796 ns / 0 B |
| Write UTF-8 string | 27.075 ns / 384 B | 12.642 ns / 0 B |
| Write 4 KiB byte array | 181.978 ns / 8,840 B | 33.759 ns / 0 B |
| Read integers | 11.580 ns / 40 B | 9.937 ns / 0 B |
| Read UTF-8 string | 23.371 ns / 96 B | 22.608 ns / 96 B |
| Read 4 KiB byte array (owned) | 11.217 ns / 112 B | 10.779 ns / 112 B |
| Read 4 KiB byte array (borrowed) | not available | 2.562 ns / 0 B |
| Read representative RPC payload | 46.484 ns / 96 B | 29.316 ns / 0 B |

The string result and owning byte-array result remain allocations because the returned `string`/`byte[]` must be materialized. The original column is the run captured before changing Core; rerunning the project after the change measures the current implementation. The exact final run is reproducible with `dotnet run -c Release --project benchmarks/AstraNet.Benchmarks`.

## Wire protocol

Each frame has a 4-byte little endian signed length, followed by exactly that many packet bytes. Valid lengths are **1 through 1,048,576**, including the packet envelope but excluding the four length bytes. The reader loops until each header/body is complete, handles coalesced frames independently, and treats EOF between frames as clean disconnect. EOF within a frame is a protocol error. Cancellation during a frame read closes the connection so later reads cannot interpret a partial frame as a new header.

Every packet starts with a one-byte kind. All numeric envelope fields use little endian encoding.

| Kind | Direction | Fields after kind |
| --- | --- | --- |
| 1: Hello | Server → client | `uint32 connectionId` |
| 2: UserMessage | Either | `uint32 messageId`, typed payload |
| 3: ServerRpc | Client → server | `uint32 objectId`, `uint16 behaviourId`, `uint32 rpcId`, arguments |
| 4: ClientRpc | Server → clients | `uint32 objectId`, `uint16 behaviourId`, `uint32 rpcId`, arguments |
| 5: State | Server → clients | `uint32 objectId`, `uint16 behaviourId`, all SyncVar values |

`ConnectAsync` waits for a valid Hello with a nonzero unique connection ID. The server exposes a connection to application traffic only after Hello has been written. Unknown packet kinds, disallowed directions, unknown message/object/behaviour/RPC IDs, and malformed payloads close the offending connection. Errors are available through `Error` and `LastError`; a malformed client does not stop the server accepting other connections.

Each connection has one reader and a single writer with at most 64 queued frames plus one active write. Concurrent sends cannot interleave frames. A full outgoing queue throws `NetworkBackpressureException`; active writes have a 10-second deadline, and client connect/Hello also has a 10-second deadline. The server accepts at most 128 concurrent peers by default (`new NetworkServer(maxConnections: ...)` changes this). An idle established connection has no inactivity timeout.

### TCP, reliable UDP, and unreliable UDP

TCP remains the default. It provides a reliable, ordered byte stream, congestion control, and straightforward framing, which fits RPCs and authoritative snapshots when simplicity matters. Its weakness for real-time traffic is head-of-line blocking: one lost segment delays every later frame, including state that may already be stale.

Reliable UDP is opt-in with `NetworkTransportKind.ReliableUdp`. It keeps datagram boundaries and retransmits only the selected `ReliableOrdered` messages, so an application can choose which traffic pays the ordering cost. It does not provide TCP's congestion-control or encryption stack, and this implementation intentionally bounds the in-flight window and datagram size.

`DeliveryMode.Unreliable` uses the same UDP session without retransmission or duplicate suppression. Dropped state updates are expected; newer updates can continue while an older one is missing. TCP maps this mode to its ordered stream for API compatibility, so use UDP when selective loss semantics are required.

### Reliable UDP wire format

Every UDP datagram is at most 1,200 bytes. The fixed 23-byte little-endian header is:

```text
magic:u16 | version:u8 | flags:u8 | connectionId:u32 |
sequence:u32 | ack:u32 | ackBits:u32 | channel:u8 |
payloadLength:u16 | payload (0..1177 bytes)
```

The handshake is a zero-sequence request/response pair. Established peers exchange data on channel `0` (`Unreliable`) or channel `1` (`ReliableOrdered`); a disconnect datagram removes the server session immediately, while the server reaper also closes sessions that have been idle for 30 seconds. Invalid magic/version/flags, lengths, channels, IDs, handshake fields, and sequence values are rejected.

For reliable packets, `sequence` starts at 1 and skips zero on wraparound. `ack` is the newest received reliable sequence and `ackBits` records the preceding 32 sequence values. A sender keeps at most 32 reliable packets pending, retransmits after 100 ms, and fails delivery after 8 seconds. The receiver tracks a reorder window, buffers newer packets until the missing sequence arrives, advances the delivery cursor, and drops packets older than that cursor or already buffered. ACK-only packets make lost data ACKs recoverable without an application payload.

The unit-test simulator is deterministic (seeded hash decisions) and can independently inject percentage loss, duplicate datagrams, pair reordering, base latency, and jitter. The 1,000-message test combines 10% loss, duplicates, reordering, and latency/jitter; a separate test uses 30% loss for Unreliable traffic.

## How IL weaving works

The weaver reads the compiled assembly and validates the complete networking model before writing a replacement. It recognizes only the AstraNet attributes, then generates controlled code for each behaviour:

1. It keeps the original public RPC method identity as a wrapper and moves the user's body to a private `__AstraNet_<Name>_<ID>_Impl` method. Parameters, locals, branch targets, exception regions, and available portable PDB source information follow the implementation.
2. The wrapper checks runtime role and attachment. A client calling ServerRpc serializes arguments and sends a request; a server calling ServerRpc runs its local implementation. Only a server may invoke a ClientRpc wrapper, which broadcasts without also running its body on the server.
3. Generated virtual dispatch methods compare numeric IDs, deserialize all arguments, check complete payload consumption, and call the preserved implementation directly. Unknown IDs return false to the runtime. No client-supplied method name is resolved.
4. Generated state readers/writers handle SyncVar fields, and generated static codecs handle custom value types. These use typed IL instructions and generic delegate calls.

RPC IDs are deterministic 32-bit FNV-1a hashes of UTF-8 text in the form `Cecil.TypeFullName::Method(Cecil.ParameterTypeFullName,...)`. Parameter types disambiguate overloads. Hash collisions within a behaviour fail the build with a member diagnostic. These IDs are routing identifiers, not authorization credentials.

Proven direct `this.Method(...)` recursion inside a received body stays in its local implementation. Calls to the same RPC on another instance retain that instance's normal routing and role checks. Unproven aliases of `this` retain wrapper semantics.

The assembly is marked with `AstraNetWovenAttribute`. Repeating the same weaver version does not duplicate methods or change an already woven assembly; a different marker version requires a clean rebuild. Portable PDBs retain a stable final filename rather than the temporary staging path. Private nested struct registrations use generated helper types without changing user visibility or running enclosing user static constructors early. Weaver diagnostics use `ASTRANET001`; setting `ASTRANET_WEAVER_TRACE=1` includes an exception stack for diagnosis. The CLI is also callable directly for inspection:

```sh
dotnet src/AstraNet.Weaver/bin/Debug/net8.0/AstraNet.Weaver.dll path/to/Consumer.dll path/to/references
```

## SyncVar replication and threading

SyncVars use **explicit full snapshots**. `server.ReplicateAsync(objectId)` calls generated writers for each behaviour on that object and sends snapshots to all currently ready clients. A direct `Health = 90` assignment becomes visible when the next snapshot is sent. There are no generated dirty bits, intercepted field assignments, change callbacks, or implicit replication timer in the library. The example supplies a timer.

Generated readers decode every field into locals and validate the entire payload before assigning any field, so a malformed snapshot cannot partially replace behaviour state. Snapshot atomicity is per behaviour, not across all behaviours on an object.

Received RPCs for the same behaviour and snapshot encoding lock that behaviour instance. Client RPC/state application also locks its instance. Callbacks have no guaranteed thread affinity: receive dispatch normally runs on network continuations, while a send error can be reported on its calling thread. Application code concurrently accessing fields or invoking authoritative local methods should also use `lock (behaviour)`. Do not hold unrelated locks while waiting for work that requires network callbacks. User message handlers across different server connections can run concurrently.

`DisconnectAsync`/`DisposeAsync` cancel socket operations and await cleanup. A lifecycle call from its own networking callback initiates shutdown and returns without waiting for that same callback; an external call can await full completion. Server-side `connection.Disconnect()` closes one peer. Client instances can reconnect after awaiting `DisconnectAsync`; dispose is terminal.

## Automatic consumer build integration

In a consumer project inside this repository, set:

```xml
<PropertyGroup>
  <AstraNetWeave>true</AstraNetWeave>
</PropertyGroup>
<ItemGroup>
  <ProjectReference Include="../../src/AstraNet.Runtime/AstraNet.Runtime.csproj" />
</ItemGroup>
```

Outside this repository, also import `build/AstraNet.Weaver.targets` using its actual location and supply the consumer's normal `TargetFramework` property. Enable weaving on the assembly that **declares** the behaviours/messages, such as `AstraNet.Example.Shared`.

The target adds the Weaver project as a build dependency with `ReferenceOutputAssembly=false`. It runs **after CoreCompile and before output copying**, rewriting the intermediate implementation DLL and its portable PDB so normal builds, project references, tests, and publish use the woven implementation. Reference assemblies remain compiler-produced. MSBuild establishes the project order; the weaving target never recursively invokes a consumer build. The integration uses [MSBuild's documented target ordering hooks](https://learn.microsoft.com/en-us/visualstudio/msbuild/how-to-extend-the-visual-studio-build-process?view=visualstudio).

Use `dotnet clean` followed by `dotnet build` after changing the weaver implementation/version or when intentionally replacing woven output. Consumer source changes naturally compile a fresh assembly before weaving. Design-time builds skip weaving.

## Tests and limits

`dotnet test` runs actual woven test assemblies. Unit tests inspect emitted IL, execute RPC wrappers/bodies, check overload IDs, round-trip primitives/custom structs/enums, verify idempotence, reject unsupported shapes without changing input files, and exercise framing with real fragmented/coalesced TCP traffic. Integration tests use real loopback sockets and multiple clients to check authoritative RPCs, state replication, exactly-once ClientRpc execution during healthy connections, user messages, disconnect/reconnect, and malformed peer isolation. Test waits have explicit deadlines.

Intentional limitations:

- Reliable UDP currently supports only `Unreliable` and `ReliableOrdered`; there is no reliable-unordered or sequenced channel, fragmentation above 1,177-byte UDP payloads, congestion control, reconnect replay, persistent delivery, tick synchronization, compression, encryption, or authentication. A successful write is not an acknowledgement of remote application execution.
- No ownership rules: any connected client can invoke any registered ServerRpc. Application authorization and argument range validation are the application's responsibility.
- No dynamic object spawning/despawning or schema negotiation. Peers must register matching identities and use matching behaviour/field schemas; newly joined clients need the next explicit snapshot. Do not change field order or RPC signatures on just one peer.
- RPCs must be synchronous, nonstatic, nonvirtual, nongeneric `void` instance methods. Async RPCs, return values, ref/in/out parameters, generic behaviours, and behaviour inheritance beyond direct `NetworkBehaviourBase` are rejected.
- Mutable, nongeneric custom structs must be defined in the assembly being woven. Readonly struct fields, reference-type graphs, nullable value types, arbitrary arrays/collections, decimal, DateTime, and generic custom serializers are not supported. Null string/byte-array values are supported.
- Avoid distinct external enum types with identical namespace/type names (an `extern alias` scenario): serializer discovery keys use full type names within a consumer schema.
- Strong-name-signed consumers and nonportable PDB formats are unsupported. The preserved source locations refer to generated implementation methods; a complete IDE debugging integration is not provided.
- Outgoing void RPC wrappers synchronously wait for the bounded transport write. Slow peers can delay a broadcast, and a broadcast may reach some clients before another client's write fails. There is no distributed transaction or rollback.
- Shutdown cancels framework I/O; it cannot terminate an application callback that blocks permanently.
- Runtime and build integration are delivered as repository projects/targets, without NuGet packaging. Validation in this workspace uses Windows and .NET SDK 8.0.423; other platforms require their own execution check.

See [docs/VALIDATION.md](docs/VALIDATION.md) for the commands, final counts, and bugs found during executable verification.
