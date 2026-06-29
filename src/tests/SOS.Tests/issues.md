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

## clrma-lldb

**Configuration:** `!clrma` under the **lldb** host (any flavor).

`clrma` drives the native CLRMA managed-analysis provider (the path Watson / `!analyze` uses). The provider
is surfaced as a command by the dbgeng (cdb) host and by the managed dotnet-dump host, but the lldb SOS
plugin (`src/SOS/lldbplugin/soscommand.cpp`) does not register it, so `sos clrma` is reported as
`Unrecognized SOS command 'clrma'`.

**Root cause / status:** Host capability gap, not a harness bug. The lldb plugin exposes a curated command
set and omits `clrma`; the underlying native entry point is dbgeng/managed-host oriented. Wiring `clrma`
into the lldb plugin is a separate SOS feature change.

**Test handling:** `DiagnosticCommandTests.Clrma_DrivesManagedAnalysis` runs in full on cdb and dotnet-dump;
`KnownIssues.SkipClrmaOnLldb` skips it on the lldb host.

## gchandleleaks-windows-only

**Configuration:** `!gchandleleaks` on every **non-Windows** host (lldb and dotnet-dump on Linux/macOS).

`gchandleleaks` is compiled `#ifndef FEATURE_PAL`, is registered only by `WindowsSOSCommand` (filtered to
`target.OperatingSystem == OSPlatform.Windows`), and is absent from `src/SOS/Strike/sos_unixexports.src`, so
it is not exported by the Unix SOS library at all. On Linux/macOS both the lldb and dotnet-dump hosts report
`Unrecognized SOS command 'gchandleleaks'`.

**Root cause / status:** Not a bug. The command is Windows-only by design.

**Test handling:** `ObjectGcHelperTests.GcHandleLeaks_RunsHandleScan` runs on Windows (cdb + dotnet-dump);
`KnownIssues.SkipGcHandleLeaksOffWindows` skips it on every non-Windows host.

## enummem-lldb

**Configuration:** `!enummem` under the **lldb** host (any flavor).

`enummem` (the `ICLRDataEnumMemoryRegions.EnumMemoryRegions` test command) is exported by the Unix SOS
library and runs on the dbgeng and dotnet-dump hosts, but it is not among the commands the lldb SOS plugin
registers, so `sos enummem` is reported as `Unrecognized SOS command 'enummem'`.

**Root cause / status:** Host capability gap, not a harness bug — same curated-command-set limitation as
`clrma` above.

**Test handling:** `MiscCommandTests.SessionCommands_Execute` asserts `dbgout` and `sosflush` on every host,
then `KnownIssues.SkipEnumMemOnLldb` skips the trailing `enummem` assertion on the lldb host.

## do-alias-lldb

**Configuration:** the `do` alias for `!dumpobj` under the **lldb** host (any flavor).

lldb ships a built-in `do` command (`Select a newer stack frame`). That built-in shadows the SOS `do` alias,
so `sos do <addr>` can't be dispatched to `dumpobj` through the lldb host and comes back as an unknown SOS
command. Other SOS aliases (for example `pe` for `printexception`) are unaffected — only `do` collides with
an lldb built-in. The primary `dumpobj` spelling works fully on lldb.

**Root cause / status:** lldb name collision, not a harness or SOS bug. The alias is intrinsically
unavailable on lldb.

**Test handling:** `ObjectInspectionTests.DumpObj_Mt_Class_Md_Chain` exercises `dumpobj`/`dumpmt`/`dumpclass`/
`dumpmd` in full on every host; the `do`-alias assertion is performed last and `KnownIssues.SkipDoAliasOnLldb`
skips just that final check on the lldb host.

## clrstack-f-singlefile-runtime-frames

**Configuration:** `!clrstack -f` under a **native host** (cdb/lldb) on a **SingleFile** target.

`-f` correctly interleaves native frames with the managed stack (so it is strictly larger than plain
`clrstack`, and every plain managed frame is preserved), but none of those native frames match the
native-runtime module names (`coreclr!`, `clr!`, `ntdll!`, …). In a self-contained single-file publish the
runtime is statically linked into the application executable, so runtime frames render under the app module
name (for example `SimpleThrow!___lldb_unnamed_symbol…`) rather than `coreclr!`.

**Root cause / status:** Not a bug. It is a direct consequence of how self-contained single-file links the
runtime into the app image; the "native runtime module" frames simply don't exist as a separate module.

**Test handling:** `ClrStackFullTests.ClrStack_Full` still asserts native interleaving on single-file
(`-f` strictly larger than plain `clrstack`, all managed frames preserved and assembly-qualified). The
`IsNativeRuntime` module-name check is asserted only for non-single-file flavors, where the runtime is its
own module.

## bpmd-singlefile-live-lldb

**Configuration:** any **live** test on the **lldb** host against a **SingleFile** target — i.e. every test
that launches the debuggee and navigates to a managed stop point with `bpmd` (the shared stop-point system,
and `LiveBpmdTests.RawBpmd_BreaksOnArbitraryMethod`).

`bpmd` arms a JIT/prestub notification breakpoint on a CoreCLR routine and resumes until the managed method
is jitted and entered. Under lldb against a self-contained single-file publish the breakpoint never binds:
the debuggee runs straight past every managed stop point (and, for the divzero target, on to its crash), so
`RunToBpmd`/`RunToBreakpoint` report `Debuggee exited before hitting bpmd …`.

**Root cause / status:** Not a harness bug. In a self-contained single-file publish the runtime is
statically linked into the application executable, whose symbols are stripped, so lldb has no symbol to
place bpmd's notification breakpoint on. The .NET Core flavor keeps CoreCLR as a distinct `libcoreclr.so`
module, so the same notification breakpoint resolves and the identical tests pass there. The dump path
exercises the same single-file managed state from a captured snapshot, so single-file coverage is retained
via the dump host; only the *live* single-file navigation is unavailable on lldb.

**Test handling:** enforced once in the harness rather than per test — `LiveTarget` throws a dynamic skip
(`HarnessSkipException`) for the `Host.Lldb` + `Flavor.SingleFile` combination from both live navigation
entry points (`GoToStopPointCore` → `RunToBpmd`, and `RunToBreakpoint`), so every affected row is reported
as skipped. Live single-file tests that do **not** depend on bpmd (e.g. run-to-crash) are unaffected and
continue to run.
