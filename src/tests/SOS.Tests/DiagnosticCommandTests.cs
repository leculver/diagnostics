// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SOS.TestHarness;
using Xunit;

namespace SOS.Tests;

/// <summary>
/// Diagnostic / status commands the legacy <c>.script</c> suite exercised: <c>!dumpgcdata</c>,
/// <c>!sosstatus</c>, the <c>!logopen</c>/<c>!logging</c>/<c>!logclose</c> logging controls, and
/// <c>!clrma</c> (the CLRMA managed-analysis provider that drives Watson / !analyze). These report or toggle
/// session state rather than inspect objects, so the assertions check the documented banners/state.
/// </summary>
public sealed class DiagnosticCommandTests
{
    public static TheoryData<Host, Flavor, Liveness> Matrix => Targets.BuildMatrix();
    public static TheoryData<Host, Flavor, Liveness> DotnetDumpMatrix => Targets.BuildMatrix(Flavor.AllValid, Host.DotnetDump);

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task DumpGcData_ReportsGcStatistics(Host host, Flavor flavor, Liveness liveness)
    {
        using Target target = await Targets.GetTargetAsync(TargetCatalog.Scenarios, host, flavor, liveness);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        target.Sos("dumpgcdata").AssertContains("concurrent GCs");
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task SosStatus_ReportsTargetAndRuntime(Host host, Flavor flavor, Liveness liveness)
    {
        using Target target = await Targets.GetTargetAsync(TargetCatalog.Scenarios, host, flavor, liveness);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        SosOutput status = target.Sos("sosstatus");
        status.AssertContains("Target OS:");
        Assert.Contains(".NET", status.Text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Logging_ReportsState(Host host, Flavor flavor, Liveness liveness)
    {
        using Target target = await Targets.GetTargetAsync(TargetCatalog.Scenarios, host, flavor, liveness);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        // logging reports the internal-logging state (a native SOS command, both hosts).
        target.Sos("logging").AssertContains("Logging");
    }

    [Theory]
    [MemberData(nameof(DotnetDumpMatrix))]
    public async Task LogOpenClose_CyclesConsoleLog(Host host, Flavor flavor, Liveness liveness)
    {
        using Target target = await Targets.GetTargetAsync(TargetCatalog.Scenarios, host, flavor, liveness);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        // logopen/logclose are managed extension commands (the dbgeng host uses native .logopen instead).
        string logFile = Path.Combine(Path.GetTempPath(), $"soslog-{Guid.NewGuid():N}.txt");
        try
        {
            target.Sos($"logopen {logFile}").AssertContains("logging to");
            target.Sos("logclose");
        }
        finally
        {
            File.Delete(logFile);
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Clrma_DrivesManagedAnalysis(Host host, Flavor flavor, Liveness liveness)
    {
        using Target target = await Targets.GetTargetAsync(TargetCatalog.Scenarios, host, flavor, liveness);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        // clrma drives the CLRMA provider (used by Watson / !analyze) and prints the managed thread analysis.
        SosOutput clrma = target.Sos("clrma");
        clrma.AssertContains("Managed analysis provider");
        clrma.AssertContains("OSThreadId:");
    }
}
