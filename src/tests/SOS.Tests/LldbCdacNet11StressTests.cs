// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SOS.TestHarness;
using Xunit;

namespace SOS.Tests;

public sealed class LldbCdacNet11StressTests
{
    [Fact]
    public async Task Lldb_Cdac_Net11_ReusesDumpHosts_UnderRepeatedCommands()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("SOSHARNESS_RUN_CDAC_NET11_LLDB_STRESS") != "1",
            "Set SOSHARNESS_RUN_CDAC_NET11_LLDB_STRESS=1 for local LLDB/cDAC/net11 stress.");

        int iterations = 100;
        string? requestedIterations = Environment.GetEnvironmentVariable("SOSHARNESS_STRESS_ITERATIONS");
        if (!string.IsNullOrEmpty(requestedIterations) && int.TryParse(requestedIterations, out int parsedIterations) && parsedIterations > 0)
        {
            iterations = parsedIterations;
        }

        TestConfig config = new(
            TargetCatalog.Scenarios,
            Host.Lldb,
            Flavor.Core,
            Liveness.Dump,
            GcType.Workstation,
            DumpKind.Full,
            publicSymbols: false,
            CoreVersion.Net11,
            Dac.CDac);

        for (int iteration = 0; iteration < iterations; iteration++)
        {
            using Target target = await Targets.GetTargetAsync(config);
            target.GoToStopPoint(TargetCatalog.StopHeap);

            AssertCommandHasOutput(target.Sos("runtimes"));
            AssertCommandHasOutput(target.Sos("eeversion"));
            AssertCommandHasOutput(target.Sos("sosstatus"));
            AssertCommandHasOutput(target.Sos("clrthreads"));
            AssertCommandHasOutput(target.Sos("dumpheap -stat"));
            AssertCommandHasOutput(target.Sos("eeheap -gc"));
            AssertCommandHasOutput(target.Sos("clrstack"));

            ulong marker = target.FindUniqueObject("FieldMarker");
            AssertCommandHasOutput(target.Sos($"dumpobj {marker:x}"));
            AssertCommandHasOutput(target.Sos($"gcroot {marker:x}"));
        }
    }

    private static void AssertCommandHasOutput(SosOutput output) =>
        Assert.NotEmpty(output.Text);
}
