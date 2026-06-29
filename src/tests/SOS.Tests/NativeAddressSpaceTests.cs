// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SOS.TestHarness;
using Xunit;

namespace SOS.Tests;

/// <summary>
/// The native-address-space commands that lean on the debugger's <c>!address</c> memory-region service:
/// <c>!maddress</c> (virtual-address-space breakdown), <c>!findpointersin</c> (GC pointers within a region
/// kind) and <c>!gctonative</c> (GC objects pointing at native ranges). All three are <b>cdb-only</b> —
/// dotnet-dump has no memory-region service ("only supported under windbg/cdb") — and need full OS symbols
/// (<c>ntdll.pdb</c>) to tag the address space, so they run under <see cref="FactRequiresOSSymbolsAttribute"/>
/// (which sources symbols from the public msdl server and auto-skips where it isn't reachable, e.g. CI).
/// <c>!findpointersin</c>/<c>!gctonative</c> have no native export, so under cdb they dispatch through the
/// managed extension via the <c>!sos</c> prefix.
///
/// <para><c>!notreachableinrange</c> (a <c>!finalizerqueue</c> helper) needs no memory-region service, so
/// it lives with the dotnet-dump-hosted managed commands below and runs in CI like the rest.</para>
/// </summary>
public sealed class NativeAddressSpaceTests
{
    public static TheoryData<TestConfig> DotnetDumpMatrix => TestConfig.BuildMatrix([TargetCatalog.Scenarios], Flavor.AllValid, Host.DotnetDump);

    // The OS-symbol Facts run against one fixed configuration: a cdb dump of the Core Scenarios target with
    // the public-symbol carveout enabled.
    private static readonly TestConfig s_osSymbolConfig =
        new(TargetCatalog.Scenarios, Host.Cdb, Flavor.Core, Liveness.Dump, publicSymbols: true);

    [FactRequiresOSSymbols]
    public async Task MAddress_SummarizesAddressSpace()
    {
        using Target target = await Targets.GetTargetAsync(s_osSymbolConfig);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        // -summary collapses the per-region rows into a per-kind histogram with a grand total. The CLR
        // regions (GCHeap, the loader heaps) plus OS regions (Stack/Teb/Peb) are tagged only when ntdll
        // symbols resolve - which is exactly what this test gates on.
        SosOutput summary = target.Sos("maddress -summary");
        summary.AssertContains("Memory Type");
        summary.AssertContains("GCHeap");
        summary.AssertContains("Stack");
        summary.AssertContains("[TOTAL]");
    }

    [FactRequiresOSSymbols]
    public async Task FindPointersIn_ScansRegionForGcPointers()
    {
        using Target target = await Targets.GetTargetAsync(s_osSymbolConfig);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        // Managed-only command -> dispatch via the !sos prefix under cdb. Stack regions always carry GC
        // pointers (managed frames root objects), so the scan has work to do.
        SosOutput found = target.Sos("sos findpointersin Stack");
        found.AssertContains("Scanning for pinned objects...");
        found.AssertContains("Stack");
    }

    [FactRequiresOSSymbols]
    public async Task GcToNative_WalksHeapForNativePointers()
    {
        using Target target = await Targets.GetTargetAsync(s_osSymbolConfig);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        SosOutput result = target.Sos("sos gctonative Stack");
        result.AssertContains("Walking GC heap to find pointers...");
        result.AssertContains("Stack Regions");
    }

    [MatrixTheory]
    [MemberData(nameof(DotnetDumpMatrix))]
    public async Task NotReachableInRange_ScansPointerRange(TestConfig config)
    {
        using Target target = await Targets.GetTargetAsync(config);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        // notreachableinrange treats [start,end) as an array of object pointers (it backs !finalizerqueue)
        // and reports the dead ones. Point it at a known live object's span: the command computes the live
        // set and emits the dumpheap-style listing, proving the scan path works.
        ulong marker = target.FindUniqueObject("FieldMarker");
        SosOutput scan = target.Sos($"notreachableinrange {marker:x} {marker + 0x200:x}");
        scan.AssertContains("Calculating live objects");
    }
}
