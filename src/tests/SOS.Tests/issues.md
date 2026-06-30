# SOS.Tests known issues

Documented bedrock findings for the modern SOS test harness (`SOS.Tests`). Each anchor is referenced
from an inline guard in the test (a small `if (…) return;` with a comment) so the condition, the reason,
and this note stay together and greppable. Where a known-unsupported command is the *last* assertion in a
test, the test verifies everything that works on that config and then returns early — it passes (not
skipped) rather than discarding the assertions that already succeeded.

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
"object moves across generations" assertion still runs there. Both `GcWhere_Moves` and `GcWhere_Structure`
source a dump-only matrix (`DumpMatrix`, `Liveness.Dump`), so no live rows are generated.

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

## cdac-net11-notimpl

**Configuration:** **net11** + **cDAC** + **dotnet-dump** host — `MiscCommandTests.SessionCommands_Execute`.

Some SOS maintenance commands (`sosflush` / `enummem`) return `E_NOTIMPL` (`0x80004001`, surfaced as
`ERROR: Unrecognized SOS command`) under the cDAC on net11.

**Root cause / status:** A cDAC defect on net11 (missing contract implementation), out of scope for the
harness; baselined pending a runtime/cDAC fix.

**Test handling:** `MiscCommandTests.SessionCommands_Execute` asserts `dbgout`, then returns early before the
`sosflush`/`enummem` assertions on net11 + cDAC + dotnet-dump (passes, not skipped), pending a runtime/cDAC fix.

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
