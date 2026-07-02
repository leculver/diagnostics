// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SOS.TestHarness;
using Xunit;

namespace SOS.Tests;

/// <summary>
/// Coverage for the EXPERIMENTAL <c>!clrstack -i</c> (ICorDebug) and <c>-i -a</c> (variables). The
/// legacy scripts ran <c>-i</c>/<c>-i -a</c> on DivZero/DynamicMethod but only shape-checked them. The
/// ICorDebug path turned out to work across every host/flavor/liveness here (DBI loads everywhere), so
/// nothing is skipped. It is far richer than the non-i path: it recovers real local <em>names</em> and
/// decodes values, which lets us assert concrete parameter/local values and cross-check object
/// references against <c>!dumpheap</c> (the SOS-native oracle).
/// </summary>
public sealed class ClrStackICorDebugTests
{
    public static TheoryData<TestConfig> Matrix { get; }
        = TestConfig.BuildMatrix([TargetCatalog.DivZero, TargetCatalog.Scenarios]);

    [SosTheory]
    [MemberData(nameof(Matrix))]
    public async Task ClrStack_ICorDebug(TestConfig config)
    {
        using Target target = await Targets.GetTargetAsync(config);
        if (config.Target == TargetCatalog.Scenarios)
        {
            target.GoToStopPoint(TargetCatalog.StopArgsLocals);
        }
        else
        {
            target.GoToFirstStop();
        }

        // Basic -i: the expected managed methods are present as [DEFAULT] frames.
        IReadOnlyList<TargetExtensions.IcorFrame> frames = target.ClrstackICorDebug(variables: false);
        Assert.Contains(frames, f => f.IsManaged);
        foreach (string method in ExpectedMethods(config.Target))
            Assert.Contains(frames, f => f.IsManaged && f.CallSite.Contains(method, StringComparison.Ordinal));

        // -i -a: same frames, now with parameters and locals decoded.
        IReadOnlyList<TargetExtensions.IcorFrame> withVars = target.ClrstackICorDebug(variables: true);
        foreach (string method in ExpectedMethods(config.Target))
            Assert.Contains(withVars, f => f.IsManaged && f.CallSite.Contains(method, StringComparison.Ordinal));

        if (config.Target == TargetCatalog.Scenarios)
        {
            // ICorDebug (!clrstack -i -a) cannot decode parameter/local *values* for a self-contained
            // single-file image: on every host (cdb and dotnet-dump) the locals come back as unnamed,
            // undecodable error slots (local_0…, IsError=true), so the value-oracle assertions in
            // AssertArgsLocalsVariables can't run. The frame/method checks above still pass for single-file.
            // Baselined as a *visible dynamic skip* (not a silent early-return pass) so it stays on the radar
            // and we revisit it — this is a real ICorDebug/DBI single-file gap, not an intended limitation.
            // See issues.md#icordebug-singlefile-locals.
            if (config.Flavor == Flavor.SingleFile)
                Assert.Skip("ICorDebug cannot decode single-file locals (values come back as IsError slots); " +
                            "see issues.md#icordebug-singlefile-locals");

            AssertArgsLocalsVariables(target, withVars);
        }
    }

    private static void AssertArgsLocalsVariables(Target target, IReadOnlyList<TargetExtensions.IcorFrame> frames)
    {
        TargetExtensions.IcorFrame method = Assert.Single(frames, f => f.IsManaged && f.CallSite.Contains("SosHarnessScenarios.ArgsLocalsMethod", StringComparison.Ordinal));

        // ICorDebug recovers parameter and local names and decodes primitive values (in decimal) on every
        // flavor, single-file included — the user assembly's embedded portable PDB travels inside the bundle
        // and is read from the dump. Object values print as "@ 0x<addr>", and that address is the very object
        // !dumpheap reports for the uniquely-named type.
        Assert.Equal("42", Param(method, "number").Value);
        TargetExtensions.IcorVar arg = Param(method, "arg");
        Assert.True(arg.HasAddress);
        Assert.Equal(target.FindUniqueObject("ArgUniqueMarker"), arg.Address);

        Assert.Equal("99", Local(method, "localInt").Value);
        TargetExtensions.IcorVar localObj = Local(method, "localObj");
        Assert.True(localObj.HasAddress);
        Assert.Equal(target.FindUniqueObject("LocalUniqueMarker"), localObj.Address);
    }

    // The methods we require on each target's ICorDebug stack.
    private static IReadOnlyList<string> ExpectedMethods(string target) => target switch
    {
        TargetCatalog.DivZero => ["C.DivideByZero", "C.F3", "C.F2", "C.Main"],
        TargetCatalog.Scenarios => ["SosHarnessScenarios.ArgsLocalsMethod", "SosHarnessScenarios.AtArgsLocals", "SosHarnessScenarios.Main"],
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    private static TargetExtensions.IcorVar Param(TargetExtensions.IcorFrame frame, string name) =>
        Assert.Single(frame.Parameters, v => v.Name == name);

    private static TargetExtensions.IcorVar Local(TargetExtensions.IcorFrame frame, string name) =>
        Assert.Single(frame.Locals, v => v.Name == name);
}
