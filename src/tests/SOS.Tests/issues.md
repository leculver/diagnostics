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
