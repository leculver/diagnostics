# SOS.Tests known issues

Documented bedrock findings for the modern SOS test harness (`SOS.Tests`). Each anchor is referenced
from a `KnownIssues` skip so the condition, the reason, and this note stay together and greppable.

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

## clrstack-f-dotnet-dump-no-native-frames

**Configuration:** `!clrstack -f` under the **dotnet-dump** host (any flavor).

`-f` (full / native-interleaved stack) prints only the *managed* frames — in the full
`Assembly.dll!Namespace.Method(args) + offset [file @ line]` format — but **no native frames**
(`coreclr!`, `clr!`, `ntdll!`, …). Under the **cdb** (dbgeng) host the same command interleaves the full
native call stack, so `-f` there is strictly larger than plain `clrstack`.

**Root cause / status:** Not a bug. The dotnet-dump host is a managed-only analyzer (DebugServices) with
no native stack unwinder, so SOS returns no native frames. Native interleaving requires a native debugger
engine (dbgeng/lldb). Long-standing, expected capability difference.

**Test handling:** `ClrStackFullTests.ClrStack_Full` is **not** skipped — it asserts the host-appropriate
property: managed frames are always preserved (every plain managed frame IP appears in `-f`) and rendered
assembly-qualified; under cdb `-f` additionally contains real native-runtime frames and is strictly larger
than plain `clrstack`, while under dotnet-dump it contains none.

## clrstack-i-singlefile

**Configuration:** `!clrstack -i` / `-i -a` (ICorDebug) on a **SingleFile** target stopped at a managed
marker method — observed at the Scenarios `argslocals` stop. Crash targets (e.g. DivZero) are unaffected
and walk fully on single-file.

Two related ICorDebug-on-single-file problems at a marker stop:
1. **Truncated leaf frames.** The stackwalk hits a `[JIT Compilation: <addr>]` pseudo-frame near the top
   and drops the frames above the first fully-JIT'd user method — so the marker method (`AtArgsLocals`) and
   the frames above it are missing. `SosHarnessScenarios.ArgsLocalsMethod` and `…Main` are still present.
2. **No local variables.** Even on frames that are present, locals can't be retrieved from the
   self-contained bundle: every local comes back as `(Error 0x80004005 retrieving local variable
   'local_N')`. Parameters resolve fine (`int number = 42`, `ArgUniqueMarker arg @ 0x…`).

**Root cause / status:** Limitations of the experimental ICorDebug (`-i`) path reading IL/local metadata
and unwinding through JIT-compilation frames in a single-file bundle. Product-side, not a harness issue.

**Test handling:** `ClrStackICorDebugTests.ClrStack_ICorDebug` asserts everything that works on single-file
(managed frames produced; the reliable methods `ArgsLocalsMethod`/`Main` appear; parameter values resolve,
including the dumpheap object oracle for `arg`), then `KnownIssues.SkipIcorDebugLocalsOnSingleFile` skips the
local-variable assertions for SingleFile. The marker leaf frame (`AtArgsLocals`) is only required on Core and
Framework. DivZero is asserted in full on every flavor.

## dumpheap-min-max-decimal

**Configuration:** `!dumpheap -min` / `-max` on every host and flavor.

The size argument is parsed as **decimal**, even though the command help text says "(hex)". Verified against
the known large array by its declared `BigArraySize`: a decimal `-min`/`-max` bracket matches it; the hex
value does not.

**Root cause / status:** Not a configuration problem — consistent everywhere; only the command's help text
("hex") is out of date (a minor SOS doc bug). No product/runtime bug.

**Test handling:** `DumpHeapObjectsTests` passes `-min`/`-max` values in **decimal** (works on every
host/flavor); nothing is skipped.
