# SOS.Tests known issues

Documented bedrock findings for the modern SOS test harness (`SOS.Tests`). Each anchor is referenced
from an inline guard in the test (a small `if (…) return;` with a comment) so the condition, the reason,
and this note stay together and greppable. Where a known-unsupported command is the *last* assertion in a
test, the test verifies everything that works on that config and then returns early — it passes (not
skipped) rather than discarding the assertions that already succeeded.

## bpmd-singlefile-live-lldb

**Configuration:** any **live** test on the **lldb** host against a **SingleFile** target — i.e. every test
that launches the debuggee and navigates to a managed stop point with `bpmd` (the shared stop-point system,
and `LiveBpmdTests.RawBpmd_BreaksOnArbitraryMethod`).

`bpmd` arms a JIT/prestub notification breakpoint on a CoreCLR routine and resumes until the managed method
is jitted and entered. Under lldb against a self-contained single-file publish the breakpoint never binds:
the debuggee runs straight past every managed stop point (and, for the divzero target, on to its crash), so
`RunToBpmd`/`RunToBreakpoint` report `Debuggee exited before hitting bpmd …`.

**Root cause / status:** Not a harness bug. In a self-contained single-file publish the runtime is
statically linked into the application executable, whose symbols are stripped. The lldb SOS plugin sets its
runtime-loaded trigger with `BreakpointCreateByName("coreclr_execute_assembly", "libcoreclr.so")`
(`src/SOS/lldbplugin/services.cpp`); in a single-file image neither the `libcoreclr.so` module nor that
exported symbol exists, so `SetRuntimeLoadedCallback` returns `E_FAIL`, JIT notifications are never enabled,
and bpmd never binds. (`readelf` on a published single-file exe confirms the `.dynsym` has no defined FUNC
symbols — only `g_dacTable` survives, which is why the *dump*/DAC path still works.) The .NET Core flavor
keeps CoreCLR as a distinct `libcoreclr.so` module exporting `coreclr_execute_assembly`, so the same
notification breakpoint resolves and the identical tests pass there. The dump path exercises the same
single-file managed state from a captured snapshot, so single-file coverage is retained via the dump host;
only the *live* single-file navigation is unavailable on lldb. A genuine fix would need runtime/host-build
cooperation (retain those exports under single-file, or expose a symbol-independent notification entrypoint
via the DAC) — it cannot be resolved in diagnostics alone, since the symbols are simply gone from the image.

**Test handling:** pruned centrally in the matrix rather than skipped per test. A target whose stop points
require bpmd (a `StopKind.Snapshot`, see `TargetCatalog.NavigatesViaBpmd`) does not emit a
`(Lldb, SingleFile, Live)` row at all (`TestConfig.IsValid`), so there is no skipped-test noise — the row
never exists. Crash targets, which just run to the fault, keep their live single-file lldb coverage. As a
belt-and-suspenders guard for any explicit bpmd use whose target is crash-based (e.g.
`LiveBpmdTests.RawBpmd_BreaksOnArbitraryMethod` on the divzero target), `LiveTarget` still throws a dynamic
skip (`HarnessSkipException`) for `Host.Lldb` + `Flavor.SingleFile` from both live navigation entry points
(`GoToStopPointCore` → `RunToBpmd`, and `RunToBreakpoint`).

## cdac-enummem-notimpl

**Configuration:** **cDAC** + **dotnet-dump** host — `MiscCommandTests.SessionCommands_Execute`. cDAC only
exists on net11+, so this is in practice a net11+ row.

`enummem` (the `EnumMemoryRegions` maintenance command) returns `E_NOTIMPL` (`0x80004001`, surfaced as
`ERROR: Unrecognized SOS command`) under the cDAC on the dotnet-dump host.

**Root cause / status:** **By design.** The cDAC (a managed contract reader) does not implement the
memory-region enumeration contract that `!enummem` drives; that command exists to exercise the native DAC's
`EnumMemoryRegions` dump-writing path, which the cDAC intentionally doesn't reproduce. The native (cdb) host
services `enummem` through dbgeng itself, so it remains available there — only the dotnet-dump + cDAC
combination is affected. Not a defect to fix.

**`sosflush` is NOT affected (and is not skipped).** An earlier note baselined `sosflush` alongside
`enummem`; that was wrong. The cDAC implements `IXCLRDataProcess::Flush`
(`dotnet/runtime` `src/native/managed/cdac/.../Legacy/SOSDacImpl.IXCLRDataProcess.cs` — it calls
`_target.Flush(FlushScope.All)` and propagates to the legacy process, returning `S_OK`), so `!sosflush` runs
cleanly on every host/DAC including net11 + cDAC. The test asserts it on all configs.

**Test handling:** `MiscCommandTests.SessionCommands_Execute` asserts `dbgout` and `sosflush` on every
config, then returns early before the `enummem` assertion on cDAC + dotnet-dump (passes, not skipped),
keyed on the DAC rather than a version since the cDAC is a net11+ concept. This is a permanent by-design
carve-out, not a pending-fix baseline.

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
(`restore-thread-state` branch on the runtime fork; restores the deleted assignment) but has not yet flowed
into the test runtimes.

**Test handling:** **baselined as a visible dynamic skip** (`Assert.Skip` via
`KnownIssues.SkipIfThreadStateNet11`), *not* a silent early-return pass — so the config stays visible and is
re-enabled the moment the fix lands. `ThreadState_DecodesStateFlags` skips on net11 (all hosts/flavors/DACs,
since the cDAC's `GetThreadData` is `E_NOTIMPL` and falls back to the same regressed legacy function).
Remove the guard once the runtime DAC repopulates `DacpThreadData::state`.

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

## icordebug-singlefile-locals

**Configuration:** every **SingleFile** row of `ClrStackICorDebugTests.ClrStack_ICorDebug` against the
`scenarios` target — i.e. `scenarios/{Cdb,DotnetDump}/SingleFile/{net8,net9,net10,net11}/Dump/…` (both
DACs). Host-independent: it reproduces on cdb *and* dotnet-dump, so it is not a debugger-host quirk.

`!clrstack -i -a` (ICorDebug) recovers real parameter/local **names** and decodes their **values** — the
test cross-checks decoded locals against `!dumpheap` (e.g. `localInt == 99`, `localObj`'s address matches
the uniquely-named heap object). On a self-contained **single-file** image this fails: ICorDebug returns the
frame's locals as unnamed, undecodable error slots (`local_0`, `local_1`, … with `IsError = true`, no name,
no value, no address), so the value-oracle assertions in `AssertArgsLocalsVariables` have nothing to match.
The plain frame/method walk (`-i`, and `-i -a` frame presence) still works for single-file — only the
local/parameter **decode** is missing. Non-single-file flavors (Core/Framework) decode locals fine on the
same host, and single-file frames are still covered via the `divzero` target (which asserts no locals).

**Root cause / status:** a real ICorDebug/DBI gap for self-contained single-file, not an intended
limitation. It is **not** the user PDB missing from the bundle — building the debuggee with an embedded
portable PDB (`DebugType=embedded`, so the symbols travel inside the single-file image and the dump) did
**not** change the result (locals still came back as `IsError` slots), so the decode failure is deeper in
how DBI maps a frame's locals for a bundled single-file module. Needs a runtime/DBI-side investigation.

**Test handling:** **baselined as a visible dynamic skip** (`Assert.Skip`), *not* a silent early-return
pass — so the config stays on the radar and we revisit it. `ClrStack_ICorDebug` verifies the ICorDebug
frames/methods for single-file (those pass), then skips only the locals-value oracle when
`Flavor == SingleFile`, citing this anchor. Remove the skip once DBI decodes single-file locals.
