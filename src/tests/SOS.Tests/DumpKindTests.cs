// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SOS.TestHarness;
using Xunit;

namespace SOS.Tests;

/// <summary>
/// Exercises the <see cref="DumpKind"/> matrix axis end to end: capturing a target as <see cref="DumpKind.Mini"/>
/// (a heap minidump — createdump type 2 / <c>--type Heap</c>) produces a smaller dump that SOS can still
/// load and analyze. The managed heap and thread list survive in a heap minidump, so the core inspection
/// commands keep working; this is the smoke that proves the reduced-dump capture + load path is wired.
/// (Full-dump coverage is the default for every other test; this opts a single configuration into Mini.)
/// </summary>
public sealed class DumpKindTests
{
    public static TheoryData<TestConfig> MiniMatrix =>
        TestConfig.BuildMatrix([TargetCatalog.Scenarios], Flavor.Core, Host.AllValid, Liveness.Dump, dumpKind: DumpKind.Mini);

    [Theory]
    [MemberData(nameof(MiniMatrix))]
    public async Task MiniDump_LoadsAndIsUsable(TestConfig config)
    {
        Assert.Equal(DumpKind.Mini, config.DumpKind);

        using Target target = await Targets.GetTargetAsync(config);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        // Capture+load is the smoke: a heap minidump loads and the runtime is analyzable. The thread list
        // and GC heap structure survive, so clrthreads and dumpheap's statistics header are present.
        target.Sos("clrthreads").AssertContains("ThreadCount");
        target.Sos("dumpheap -stat").AssertContains("Statistics");

        // NOTE (documented degradation): a Mini (heap) dump carries less than a Full dump — notably it does
        // not reliably enumerate every user object, so heap-walking assertions that pass on Full (e.g.
        // finding the debuggee's own FieldMarker via `dumpheap -stat`) can come up empty here. That reduced
        // fidelity is exactly why DumpKind.Mini is an opt-in axis rather than a default.
    }
}
