# Executed validation

Validated on 2026-09-05 in Windows with .NET SDK **8.0.423**, targeting **net8.0**. This is a record of commands actually executed, not a proposed test plan.

## Final results

| Command | Result |
| --- | --- |
| `dotnet restore AstraNet.sln` | All ten projects restored (including BenchmarkDotNet) |
| `dotnet build` | Success; 0 warnings, 0 errors |
| `dotnet test` | 52 unit + 28 integration = **80 passed, 0 failed, 0 skipped** |
| `dotnet build -c Release` | Fresh Release outputs woven; 0 warnings, 0 errors |
| `dotnet run -c Release --project benchmarks/AstraNet.Benchmarks` | 15 BenchmarkDotNet benchmarks completed; .NET SDK 8.0.423, .NET 8.0.29, Windows 11 |
| `dotnet run --project examples/AstraNet.ExampleServer -- --demo` | Debug demo succeeded |
| `dotnet run --no-build -c Release --project examples/AstraNet.ExampleServer -- --demo` | Release demo succeeded |
| `git diff --check` | No whitespace errors |

The final benchmark table is in the README. The exact original baseline was measured before changing Core: writer operations allocated 376 B (integers), 416 B (representative RPC), 384 B (string), and 8,840 B (4 KiB byte array); reader operations allocated 40 B (integers), 96 B (string), 112 B (owned 4 KiB byte array), and 96 B (representative RPC). Reusable primitive writes/reads and borrowed byte-array reads measured 0 B in the final run.

The repeated normal build printed `unchanged (already woven or no networking members)` for already transformed consumers. Unit tests also compare assembly and portable PDB hashes before/after another weave, ensuring no duplicate generation or file changes.

Local generated evidence is intentionally ignored by Git:

- [Release build output](../artifacts/build-release.log)
- [Release test output](../artifacts/test-release.log) and TRX files under `artifacts/test-results/release/`
- [Release demo output](../artifacts/demo-release.log)
- [Separate server output](../artifacts/example-server.log), [observer output](../artifacts/example-client-a.log), [attacking client output](../artifacts/example-client-b.log)

## Separate executable verification

Three real processes ran from their Debug build directories, without rebuilding:

```text
AstraNet.ExampleServer.exe --port 0
AstraNet.ExampleClient.exe --port 63537 --damage 0 --seconds 6
AstraNet.ExampleClient.exe --port 63537 --damage 15 --seconds 3
```

The server selected port 63537 for this run. The observer connected first. Both clients exited with code 0 and reported `Health = 85, damage effects = 1`; both executed the effect once. All three stderr logs were empty. The spawned server was stopped after the clients exited.

The finite public API demo independently checked that Client A's ServerRpc changed the authoritative server state first, both client replicas stayed at 100 before replication, both reached 85 after replication, and ClientRpc ran once per client with no server-local effect.

## Required coverage and source evidence

| Requirement | Executed evidence |
| --- | --- |
| All primitives, null/empty, float bit patterns, explicit endian layout | [SerializationTests.cs](../tests/AstraNet.UnitTests/SerializationTests.cs) |
| Multiple custom structs, nested/private fields, all eight enum widths | [SerializationTests.cs](../tests/AstraNet.UnitTests/SerializationTests.cs) |
| Invalid length, size limits, UTF-8, boolean, truncation and trailing bytes | [SerializationTests.cs](../tests/AstraNet.UnitTests/SerializationTests.cs), [ProtocolSafetyTests.cs](../tests/AstraNet.IntegrationTests/ProtocolSafetyTests.cs) |
| Actual woven wrappers, generated methods, loaded/executed IL, overload IDs | [WeaverTests.cs](../tests/AstraNet.UnitTests/WeaverTests.cs) |
| Original branches, locals, catch/finally, direct recursion, other receivers | [WeaverTests.cs](../tests/AstraNet.UnitTests/WeaverTests.cs), [TestBehaviours.cs](../tests/AstraNet.IntegrationTests/TestBehaviours.cs) |
| Portable symbols, version mismatch, repeated weaving, input-preserving rejection | [WeaverTests.cs](../tests/AstraNet.UnitTests/WeaverTests.cs) |
| Partial headers/bodies, multiple frames in one write, concurrent writes, EOF | [FramingTests.cs](../tests/AstraNet.UnitTests/FramingTests.cs) |
| Two clients, authoritative ServerRpc, SyncVars, one ClientRpc per client | [EndToEndTests.cs](../tests/AstraNet.IntegrationTests/EndToEndTests.cs) |
| Typed messages, targeted sends, broadcast, identities, reconnect | [EndToEndTests.cs](../tests/AstraNet.IntegrationTests/EndToEndTests.cs) |
| Reliable UDP handshake, disconnect, RPC, SyncVars, reliable/unreliable typed messages | [UdpEndToEndTests.cs](../tests/AstraNet.IntegrationTests/UdpEndToEndTests.cs) |
| Deterministic 10% loss + duplicates + reordering + latency/jitter, 1,000 reliable messages | [ReliableUdpTests.cs](../tests/AstraNet.UnitTests/ReliableUdpTests.cs), [DeterministicUdpNetwork.cs](../tests/AstraNet.UnitTests/DeterministicUdpNetwork.cs) |
| Deterministic 30% loss with no Unreliable retransmission | [ReliableUdpTests.cs](../tests/AstraNet.UnitTests/ReliableUdpTests.cs) |
| ACK bitfields, duplicate ACKs, sequence wraparound, datagram validation | [ReliableUdpTests.cs](../tests/AstraNet.UnitTests/ReliableUdpTests.cs) |
| UDP idle timeout/reaper cleanup | [UdpEndToEndTests.cs](../tests/AstraNet.IntegrationTests/UdpEndToEndTests.cs) |
| Unknown/direction-invalid packets; bad known RPC/state/message payloads | [ProtocolSafetyTests.cs](../tests/AstraNet.IntegrationTests/ProtocolSafetyTests.cs) |
| Hello ordering, simultaneous mutations, callback shutdown, handshake cancellation | [LifecycleTests.cs](../tests/AstraNet.IntegrationTests/LifecycleTests.cs) |

There are no intentionally skipped tests, mocks substituting for required TCP integration, or commented-out failures.

## Bugs found and fixed

1. **Cecil generic metadata context:** importing a generic serializer delegate field type independently lost its owning `!0` context and caused a weaver `NullReferenceException`. Importing the field definition before closing its declaring generic type fixed generated registrations. Primitive, enum, struct, and real RPC execution tests pass.
2. **Private nested serializer access:** direct module-initializer access to a private nested struct caused `MethodAccessException`, preventing the entire consumer module from loading. Generated nested registration helper chains now preserve access rules without widening user types or triggering enclosing static constructors. Tests include deeply nested private structs and an existing user module initializer.
3. **Handshake ordering:** an accepted connection became visible to broadcasts before its Hello was written. Ready-state filtering now exposes peers only after Hello, with a twelve-client concurrent-traffic regression.
4. **Authoritative write races:** RPCs from separate connections could update the same field concurrently. Runtime dispatch and snapshot encoding now lock each behaviour. A yielding 120-RPC test checks no lost updates and no concurrent entry.
5. **Shutdown self-wait and pending connects:** synchronous lifecycle calls from a callback could await their own receive task, and pending Hello waits could delay disposal. Shared shutdown tasks, callback scopes, and cancellation of the in-progress handshake fix both; explicit deadline tests pass.
6. **Recursive call receiver semantics:** moving every same-method call to an implementation could bypass another instance's wrapper. Only proven direct-this calls stay local; another instance retains its identity and role checks. Both paths have execution tests.
7. **Portable PDB staging path:** generated CodeView records initially named a deleted temporary directory. The symbol writer now records the stable final PDB filename; tests inspect the actual PE debug directory and preserved original-body sequence points.
8. **Example completion output:** normal timeout completion could omit the standalone client's final state summary. Both exit paths now print it, confirmed in the three-process smoke run.

One parallel development build encountered a Windows DLL file lock because two agents built shared outputs simultaneously. Consolidated builds were serialized thereafter; the final normal and Release solution builds both succeeded.

## Scope and remaining limitations

The implemented system comprises Core codecs/contracts, bounded BCL TCP and reliable-UDP transports, Runtime server/client registries and dispatch, a Mono.Cecil Weaver, an automatic MSBuild target, tests, and runnable examples. Protocol and API details are documented in [README.md](../README.md).

The library deliberately uses explicit full SyncVar snapshots and synchronous void RPC sends. It has no authentication/ownership policy, schema negotiation, dynamic spawn system, async/return-value RPCs, behaviour inheritance, generic collection codecs, reliable-unordered/sequenced UDP channels, UDP fragmentation, congestion control, or message replay across disconnects. Custom mutable structs must be in the woven consumer assembly. Signed assemblies/nonportable symbols and full IDE integration are outside the current scope. See the README's complete limitations and threading contract before extending these areas.

This run proves the listed behavior on Windows/.NET 8. It does not claim load-test capacity, cross-platform execution, or release publication.
