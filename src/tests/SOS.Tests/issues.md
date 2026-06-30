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

## singlefile-net11-sdk

**Configuration:** every **SingleFile** + **net11** row (all hosts, all tests).

A self-contained single-file net11 debuggee cannot be produced in this repo's environment. The harness
publishes single-file debuggees on demand with
`dotnet publish -r <rid> --self-contained true -p:PublishSingleFile=true -p:BuildProjectFramework=net11.0`,
and the in-repo SDK (`.dotnet`, currently 10.0.x) refuses to target net11:

```
error NETSDK1045: The current .NET SDK does not support targeting .NET 11.0.
```

The framework-dependent **Core** net11 debuggees are unaffected because they are prebuilt by the repo build
(Debuggees.proj multi-TFM, using the net11 targeting/runtime packs), not published on demand.

**Root cause / status:** Environment/build limitation, not a SOS bug. Fixing it requires either a net11 SDK
or prebuilding net11 single-file debuggees during the repo build (the morning follow-up).

**Test handling:** the `(SingleFile, net11)` combination is pruned in `TestConfig.IsValid`, so those rows are
never generated. Remove that gate once net11 single-file debuggees can be produced.

## cdac-net11-stackwalk

**Configuration:** **net11** + **cDAC** + **dotnet-dump** host — the `clrstack`-family commands
(`ClrStack_Registers`, `ClrStack_Full`, `ClrStack_FrameCount`, `ClrStack_ArgsLocals`, `ClrStack_AllThreads`,
`ClrStack_SourceLines`, `ClrStack_GcRoots`, `ClrStack_GcRoots_Flags`) and `parallelstacks`
(`ParallelStacks_GroupsThreadsByCallStack`).

Under the cDAC on net11, the managed stack walk returns no frames on the dotnet-dump host. `clrstack -r`
prints only the `OS Thread Id:` banner with no `Child SP / IP / Call Site` table; `parallelstacks` reports
`==> 0 threads with 0 roots`.

**Root cause / status:** A cDAC (managed contract DAC) defect on net11 — the runtime's DAC side, out of scope
for the harness; baselined pending a runtime/cDAC fix. Scoped narrowly because the **legacy** DAC walks
correctly, and the **lldb** host walks correctly even under the cDAC (it supplies the native register/unwind
context that the dotnet-dump managed analyzer lacks), so only the dotnet-dump cDAC path is affected.

**Test handling:** skipped via `KnownIssues.SkipCDacNet11StackwalkOnDotnetDump`.

## cdac-net11-notimpl

**Configuration:** **net11** + **cDAC** + **dotnet-dump** host — `MiscCommandTests.SessionCommands_Execute`.

Some SOS maintenance commands (`sosflush` / `enummem`) return `E_NOTIMPL` (`0x80004001`, surfaced as
`ERROR: Unrecognized SOS command`) under the cDAC on net11.

**Root cause / status:** A cDAC defect on net11 (missing contract implementation), out of scope for the
harness; baselined pending a runtime/cDAC fix.

**Test handling:** skipped via `KnownIssues.SkipCDacNet11NotImplemented`.

## dumpdomain-net11

**Configuration:** every **net11** row of `DumpDomainTests.DumpDomain_Structure` (both DACs, both non-Windows
hosts, dump and live).

On net11 `dumpdomain` no longer emits the labeled **System Domain** block the structure test asserts; the
output begins with unlabeled domain rows. This affects the **legacy** DAC as well as the cDAC, so it is a
genuine output-shape change in net11, not a DAC-specific defect.

**Root cause / status:** net11 `dumpdomain` output-format change. Whether the fix is in SOS (parse/print) or
in the test's expectations is a morning follow-up; baselined for now.

**Test handling:** skipped via `KnownIssues.SkipDumpDomainNet11`.

## clrthreads-net11

**Configuration:** every **net11** row of `MemoryAndDecodeTests.ThreadState_DecodesStateFlags` (both DACs,
both non-Windows hosts, dump and live).

The test lifts a thread-state value out of the `clrthreads` **State** column (regex
`([0-9a-fA-F]{6,8})\s+(?:Preemptive|Cooperative)`) and feeds it to `!threadstate`. On net11 the State column
is `0` for **every** thread — including the Finalizer and Threadpool Workers, which must at minimum have
`TS_Background = 0x200`. A bare `0` doesn't match (needs 6–8 hex digits), so there is nothing to decode.
Affects both DACs. The column *shape* is unchanged; only the *value* is wrong (it is the SOS-printed hex of
`DacpThreadData::state`, with no masking).

**Root cause (bedrock — dotnet/runtime regression):** dotnet/runtime PR
[#126592](https://github.com/dotnet/runtime/pull/126592) "[cDAC] Fix bug in GetThreadData"
(commit `9bb0c1ebc30`, 2026-04-11) deleted the line `threadData->state = thread->m_State;` from
`ClrDataAccess::GetThreadData` in `src/coreclr/debug/daccess/request.cpp`, with no replacement. The PR's
stated intent was about dead-thread reporting in a *different* path (`dacdbiimpl.cpp` /
`GetThreadOwningMonitorLock`), so the deletion looks like collateral. `DacpThreadData::state` is still a
field in the SOS contract (struct size pinned by `static_assert sizeof == 0x68` for back-compat) and SOS
still reads it for the State column, but the DAC now leaves it at its `ZeroMemory` default of 0.

This is **not cDAC-specific**: the cDAC's managed `SOSDacImpl.GetThreadData` returns `E_NOTIMPL`
(`src/native/managed/cdacreader/src/Legacy/SOSDacImpl.cs`), so `--usecdac` falls back to the same legacy
`GetThreadData` — hence both DACs report 0. The removal is an ancestor of the dump's build
(`11.0.0-preview.6.26319.105`, source `b756a8d8`) and has not been re-added on `release/11.0-preview6`.

**Impact beyond this test:** `!clrthreads` State is broken for *all* net11 users, not just the harness.
The fix is to restore the one deleted line in the runtime (`threadData->state = thread->m_State;`).

**Status:** root cause is a dotnet/runtime regression. A fix has been written
(`restore-thread-state` branch on the runtime fork; restores the deleted assignment). The diagnostics test
is **intentionally left un-skipped** so it fails until the fixed DAC flows into the test runtimes — that red
result is the signal that the runtime fix has not yet landed here.

**Test handling:** **not skipped.** `ThreadState_DecodesStateFlags` runs on net11 and is expected to fail
until the runtime DAC is fixed. (The former `KnownIssues.SkipThreadStateNet11` helper was removed.)

## cdac-net11-lldb-hostcrash (intermittent — observed, NOT baselined)

**Configuration:** `Lldb/Core/net11/Dump/Workstation/Full/cdac` — the lldb host + cDAC, net11 dump.

**Symptom:** very rarely, the single lldb child process that backs this per-config host dies mid-session,
after which every remaining test sharing that host fails instantly (<1 ms) with
`System.IO.IOException : Pipe is broken` at `LldbHostBase.Run`. Because all tests for a config share one
long-lived host, one native crash cascades into a large block of fast failures (47 in a full pass, fewer if
the crash lands later in the run).

**Frequency:** observed once in 12 consecutive full soak passes (~26k test-executions). A targeted isolated
re-run of exactly this config (`SOSHARNESS_ONLY_COREVERSIONS=Net11 _HOSTS=Lldb _DAC=CDac _LIVENESS=Dump`)
did **not** reproduce it — the only "failures" there were harmless `No data found` env-filter artifacts.

**Root cause / status:** an intermittent native crash on the cDAC/lldb net11 path (cDAC is a dotnet/runtime
component, so the underlying defect is out of scope for the harness). Because it is net11 and not
reproducible, it is **not** hard-skipped — doing so would forfeit the ~11/12 passing coverage this config
normally provides. Tracked here for the morning. Two follow-ups to weigh: (a) make the harness resilient by
restarting a dead per-config host and retrying the in-flight command instead of cascading `Pipe is broken`;
(b) capture the lldb/cdac crash (core or stderr) to pin the runtime-side defect.
