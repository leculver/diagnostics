# SOS.Tests known issues

Documented bedrock findings for the modern SOS test harness (`SOS.Tests`). Each anchor is referenced
from a `KnownIssues` skip so the condition, the reason, and this note stay together and greppable.

## singlefile-cdb-dac

The cdb (dbgeng) host cannot load a self-contained **single-file** dump in a hermetic test environment.

A self-contained single-file app bundles the runtime (`coreclr.dll`) inside the executable, so the
matching DAC (`mscordaccore.dll`) is not present as a discoverable file next to a runtime on disk. Raw
dbgeng/SOS resolves the DAC either next to the runtime module or by downloading it from a symbol server.
The harness deliberately runs with a **local-only** `_NT_SYMBOL_PATH` (the developer's machine typically
points `_NT_SYMBOL_PATH` at the Azure-authed `symweb` server, which both makes the run network-dependent
and crashes SOS host init while loading Azure.Identity's assembly closure). With no symbol server, dbgeng
reports:

```
Failed to load data access module, 0x80004002
...
No CLR runtime found.
```

The **dotnet-dump** host resolves the DAC through its own DebugServices logic, so single-file is fully
covered there. The cdb host skips single-file via `KnownIssues.SkipSingleFileUnderCdb`. A future
improvement could stage the matching DAC into the local symbol cache for single-file targets.

## gcwhere-live-gc

Driving the `SosHarnessScenarios` debuggee **live** through the generation-promotion markers crashes the
debuggee. `GcWhere_Moves` sets a `bpmd` on each of `AtGen0`/`AtGen1`/`AtGen2` and runs forward; the
debuggee calls a blocking `GC.Collect(2)` between the markers. With the gen-marker breakpoints armed,
continuing from `gen1` through `GC.Collect(2)` aborts the debuggee with:

```
Fatal error. Internal CLR error. (0x80131506)
   at SosHarnessScenarios.Main()
```

This is an interaction between live managed breakpoints on the GC-bracketing markers and a full blocking
GC under dbgeng (the parked worker threads in the consolidated debuggee likely aggravate it). The **dump**
path exercises the exact same gen0→gen1→gen2 progression (each stop is captured independently), so the
"object moves across generations" assertion still runs there. The live path skips via
`KnownIssues.SkipLiveGenPromotion`.
