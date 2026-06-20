// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using SOS.TestHarness;
using Xunit;

namespace SOS.Tests;

/// <summary>
/// Runtime / module / process listing commands the legacy <c>.script</c> suite exercised: <c>!eeversion</c>,
/// <c>!modules</c>, <c>!clrmodules</c>, <c>!assemblies</c>, <c>!runtimes</c>, <c>!registers</c>,
/// <c>!threads</c>, and <c>!dumpruntimetypes</c>. These are listing/identity commands, so the assertions
/// check that the expected entities (the debuggee's own module, the runtime, the worker threads, a known
/// type) appear.
/// </summary>
public sealed class RuntimeInfoTests
{
    public static TheoryData<Host, Flavor, Liveness> Matrix => Targets.BuildMatrix();
    public static TheoryData<Host, Flavor, Liveness> DotnetDumpMatrix => Targets.BuildMatrix(Flavor.AllValid, Host.DotnetDump);

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task EeVersion_ReportsRuntimeAndSosVersion(Host host, Flavor flavor, Liveness liveness)
    {
        using Target target = await Targets.GetTargetAsync(TargetCatalog.Scenarios, host, flavor, liveness);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        SosOutput ee = target.Sos("eeversion");
        Assert.Matches(@"\d+\.\d+\.\d+", ee.Text); // the runtime version
        ee.AssertContains("SOS Version:");
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task ClrModulesAndAssemblies_ListDebuggeeModule(Host host, Flavor flavor, Liveness liveness)
    {
        using Target target = await Targets.GetTargetAsync(TargetCatalog.Scenarios, host, flavor, liveness);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        // The CLR module list and the assembly list both include the debuggee, on every host.
        target.Sos("clrmodules").AssertContains("SosHarnessScenarios");
        target.Sos("assemblies").AssertContains("SosHarnessScenarios");
    }

    [Theory]
    [MemberData(nameof(DotnetDumpMatrix))]
    public async Task Modules_Registers_Threads_DotnetDumpOnly(Host host, Flavor flavor, Liveness liveness)
    {
        // modules, registers and threads are provided by the dotnet-dump REPL; the dbgeng (cdb) host uses
        // the native debugger's lm / r / ~ instead, so these names exist only under dotnet-dump.
        using Target target = await Targets.GetTargetAsync(TargetCatalog.Scenarios, host, flavor, liveness);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        target.Sos("modules").AssertContains("SosHarnessScenarios");
        target.Sos("registers").AssertContains("rsp");

        // The debuggee parks several worker threads, so the thread list has multiple entries.
        SosOutput threads = target.Sos("threads");
        Assert.True(Regex.Matches(threads.Text, @"0x[0-9a-fA-F]+\s+\(\d+\)").Count >= 2,
            $"expected multiple threads:\n{threads.Text}");
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Runtimes_ReportsLoadedRuntime(Host host, Flavor flavor, Liveness liveness)
    {
        using Target target = await Targets.GetTargetAsync(TargetCatalog.Scenarios, host, flavor, liveness);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        SosOutput runtimes = target.Sos("runtimes");
        Assert.Contains(".NET", runtimes.Text, StringComparison.Ordinal); // ".NET Core runtime" or ".NET Framework"
        Assert.Matches(@"\d+\.\d+\.\d+", runtimes.Text);
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task DumpRuntimeTypes_ListsRuntimeTypeObjects(Host host, Flavor flavor, Liveness liveness)
    {
        using Target target = await Targets.GetTargetAsync(TargetCatalog.Scenarios, host, flavor, liveness);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        SosOutput types = target.Sos("dumpruntimetypes");
        types.AssertContains("Type Name");
        types.AssertContains("System."); // at least the framework RuntimeType objects
    }
}
