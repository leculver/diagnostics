// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SOS.TestHarness;
using Xunit;

namespace SOS.Tests;

/// <summary>
/// Whole-heap analysis commands: <c>!sizestats</c> (per-generation size histogram), <c>!traverseheap</c>
/// (writes the heap graph to a CLR-Profiler file), and the ephemeral-reference scans <c>!ephrefs</c> /
/// <c>!ephtoloh</c>. (The native memory-region commands <c>!maddress</c>/<c>!gctonative</c>/
/// <c>!findpointersin</c>/<c>!notreachableinrange</c> depend on the debugger's <c>!address</c> service and
/// are deferred — see DecisionPoints.)
/// </summary>
public sealed class HeapAnalysisTests
{
    public static TheoryData<Host, Flavor, Liveness> Matrix => Targets.BuildMatrix();
    public static TheoryData<Host, Flavor, Liveness> DotnetDumpMatrix => Targets.BuildMatrix(Flavor.AllValid, Host.DotnetDump);

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task SizeStats_ReportsGenerationHistogram(Host host, Flavor flavor, Liveness liveness)
    {
        using Target target = await Targets.GetTargetAsync(TargetCatalog.Scenarios, host, flavor, liveness);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        target.Sos("sizestats").AssertContains("Size Statistics");
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task TraverseHeap_WritesProfilerFile(Host host, Flavor flavor, Liveness liveness)
    {
        using Target target = await Targets.GetTargetAsync(TargetCatalog.Scenarios, host, flavor, liveness);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        string file = Path.Combine(Path.GetTempPath(), $"traverseheap-{Guid.NewGuid():N}.out");
        try
        {
            target.Sos($"traverseheap {file}");
            Assert.True(File.Exists(file), $"traverseheap should have written {file}");
            Assert.True(new FileInfo(file).Length > 0, "the profiler file should be non-empty");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [MemberData(nameof(DotnetDumpMatrix))]
    public async Task EphemeralScans_Run(Host host, Flavor flavor, Liveness liveness)
    {
        using Target target = await Targets.GetTargetAsync(TargetCatalog.Scenarios, host, flavor, liveness);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        // ephrefs/ephtoloh are managed extension commands (dotnet-dump only).
        target.Sos("ephrefs").AssertContains("References from");
        target.Sos("ephtoloh").AssertContains("Ephemeral");
    }
}
