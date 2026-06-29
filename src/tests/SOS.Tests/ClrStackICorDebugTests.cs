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

    [MatrixTheory]
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
        foreach (string method in ExpectedMethods(config.Target, config.Flavor))
            Assert.Contains(frames, f => f.IsManaged && f.CallSite.Contains(method, StringComparison.Ordinal));

        // -i -a: same frames, now with parameters and locals decoded.
        IReadOnlyList<TargetExtensions.IcorFrame> withVars = target.ClrstackICorDebug(variables: true);
        foreach (string method in ExpectedMethods(config.Target, config.Flavor))
            Assert.Contains(withVars, f => f.IsManaged && f.CallSite.Contains(method, StringComparison.Ordinal));

        if (config.Target == TargetCatalog.Scenarios)
            AssertArgsLocalsVariables(target, withVars, config.Flavor);
    }

    private static void AssertArgsLocalsVariables(Target target, IReadOnlyList<TargetExtensions.IcorFrame> frames, Flavor flavor)
    {
        TargetExtensions.IcorFrame method = Assert.Single(frames, f => f.IsManaged && f.CallSite.Contains("SosHarnessScenarios.ArgsLocalsMethod", StringComparison.Ordinal));

        // Parameters resolve on every flavor. ICorDebug recovers names and decodes primitive values
        // (in decimal); object parameters print as "@ 0x<addr>", and that address is the very object
        // !dumpheap reports for the uniquely-named type.
        Assert.Equal("42", Param(method, "number").Value);
        TargetExtensions.IcorVar arg = Param(method, "arg");
        Assert.True(arg.HasAddress);
        Assert.Equal(target.FindUniqueObject("ArgUniqueMarker"), arg.Address);

        // ICorDebug fails to retrieve local variables from a single-file bundle (they come back as
        // anonymous "local_N" errors); see KnownIssues / issues.md#clrstack-i-singlefile.
        KnownIssues.SkipIcorDebugLocalsOnSingleFile(flavor);

        Assert.Equal("99", Local(method, "localInt").Value);
        TargetExtensions.IcorVar localObj = Local(method, "localObj");
        Assert.True(localObj.HasAddress);
        Assert.Equal(target.FindUniqueObject("LocalUniqueMarker"), localObj.Address);
    }

    // The methods we require on each target's ICorDebug stack. On single-file the marker leaf frame
    // (AtArgsLocals) is unreliable — ICorDebug truncates at a [JIT Compilation] frame — so it's only
    // required on Core/Framework (see issues.md#clrstack-i-singlefile).
    private static IReadOnlyList<string> ExpectedMethods(string target, Flavor flavor) => target switch
    {
        TargetCatalog.DivZero => ["C.DivideByZero", "C.F3", "C.F2", "C.Main"],
        TargetCatalog.Scenarios when flavor == Flavor.SingleFile => ["SosHarnessScenarios.ArgsLocalsMethod", "SosHarnessScenarios.Main"],
        TargetCatalog.Scenarios => ["SosHarnessScenarios.ArgsLocalsMethod", "SosHarnessScenarios.AtArgsLocals", "SosHarnessScenarios.Main"],
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    private static TargetExtensions.IcorVar Param(TargetExtensions.IcorFrame frame, string name) =>
        Assert.Single(frame.Parameters, v => v.Name == name);

    private static TargetExtensions.IcorVar Local(TargetExtensions.IcorFrame frame, string name) =>
        Assert.Single(frame.Locals, v => v.Name == name);
}
