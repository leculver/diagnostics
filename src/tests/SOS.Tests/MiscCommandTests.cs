// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SOS.TestHarness;
using Xunit;

namespace SOS.Tests;

/// <summary>
/// Trivial session/diagnostic commands that take no target state: <c>!dbgout</c> (toggles internal debug
/// output), <c>!sosflush</c> (resets SOS's cached state), and <c>!enummem</c> (the EnumMemoryRegions test
/// command). The assertion is that each is a recognised command that executes without error. (<c>!crashinfo</c>
/// is deferred — it needs a dump written by the runtime's crash reporter, which the harness doesn't produce.)
/// </summary>
public sealed class MiscCommandTests
{
    public static TheoryData<TestConfig> Matrix => TestConfig.BuildMatrix([TargetCatalog.Scenarios]);

    [MatrixTheory]
    [MemberData(nameof(Matrix))]
    public async Task SessionCommands_Execute(TestConfig config)
    {
        using Target target = await Targets.GetTargetAsync(config);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        // dbgout toggles internal debug logging and reports the new state.
        target.Sos("dbgout").AssertContains("Debug output logging");

        // sosflush and enummem produce no output but must be recognised commands that run cleanly.
        AssertRuns(target.Sos("sosflush"));

        KnownIssues.SkipEnumMemOnLldb(config.Host);
        AssertRuns(target.Sos("enummem"));
    }

    private static void AssertRuns(SosOutput output)
    {
        Assert.DoesNotContain("Unrecognized", output.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not extension gallery", output.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ERROR:", output.Text, StringComparison.Ordinal);
    }
}
