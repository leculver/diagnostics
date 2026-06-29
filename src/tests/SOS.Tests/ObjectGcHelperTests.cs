// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SOS.TestHarness;
using Xunit;

namespace SOS.Tests;

/// <summary>
/// Per-object GC helper commands: <c>!dumpobjgcrefs</c>, <c>!listnearobj</c>, <c>!verifyobj</c>,
/// <c>!findappdomain</c>, <c>!dumpalc</c>, <c>!pathto</c>, and <c>!gchandleleaks</c>. All anchored on the
/// debuggee's reference-rich <c>FieldMarker</c> (it points at a string, an int[], and byte[]s), so the GC
/// references and object neighbours are known.
/// </summary>
public sealed class ObjectGcHelperTests
{
    public static TheoryData<TestConfig> Matrix => TestConfig.BuildMatrix([TargetCatalog.Scenarios]);
    public static TheoryData<TestConfig> CoreRuntimeMatrix => TestConfig.BuildMatrix([TargetCatalog.Scenarios], Flavor.Core | Flavor.SingleFile);
    public static TheoryData<TestConfig> DotnetDumpMatrix => TestConfig.BuildMatrix([TargetCatalog.Scenarios], Flavor.AllValid, Host.DotnetDump);

    [MatrixTheory]
    [MemberData(nameof(DotnetDumpMatrix))]
    public async Task DumpObjGcRefs_ListsReferences(TestConfig config)
    {
        using Target target = await Targets.GetTargetAsync(config);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        // dumpobjgcrefs (the engine behind dumpobj -refs) is a managed extension command (dotnet-dump only).
        SosOutput refs = target.Sos($"dumpobjgcrefs {target.FindUniqueObject("FieldMarker"):x}");
        refs.AssertContains("TextField");
        refs.AssertContains("System.String");
        refs.AssertContains("Numbers");
        refs.AssertContains("System.Int32[]");
    }

    [MatrixTheory]
    [MemberData(nameof(Matrix))]
    public async Task ListNearObj_ShowsNeighbours(TestConfig config)
    {
        using Target target = await Targets.GetTargetAsync(config);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        ulong marker = target.FindUniqueObject("FieldMarker");
        SosOutput near = target.Sos($"listnearobj {marker:x}");
        near.AssertContains("Current:");
        Assert.Contains("FieldMarker", near.Text, StringComparison.Ordinal);
    }

    [MatrixTheory]
    [MemberData(nameof(Matrix))]
    public async Task VerifyObj_AcceptsGoodObject(TestConfig config)
    {
        using Target target = await Targets.GetTargetAsync(config);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        target.Sos($"verifyobj {target.FindUniqueObject("FieldMarker"):x}").AssertContains("is a valid object");
    }

    [MatrixTheory]
    [MemberData(nameof(Matrix))]
    public async Task FindAppDomain_ResolvesObjectDomain(TestConfig config)
    {
        using Target target = await Targets.GetTargetAsync(config);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        SosOutput domain = target.Sos($"findappdomain {target.FindUniqueObject("FieldMarker"):x}");
        domain.AssertContains("AppDomain:");
        domain.AssertContains("Name:");
    }

    [MatrixTheory]
    [MemberData(nameof(Matrix))]
    public async Task PathTo_TracesReferencePath(TestConfig config)
    {
        using Target target = await Targets.GetTargetAsync(config);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        ulong marker = target.FindUniqueObject("FieldMarker");
        ulong text = ObjectCommandParsing.Hex(target.DumpObj(marker).Field("TextField").Value);

        // FieldMarker references its TextField string directly, so the GC path goes marker -> string.
        SosOutput path = target.Sos($"pathto {marker:x} {text:x}");
        Assert.Contains(marker.ToString("x"), path.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("System.String", path.Text, StringComparison.Ordinal);
    }

    [MatrixTheory]
    [MemberData(nameof(CoreRuntimeMatrix))]
    public async Task DumpAlc_ResolvesDefaultLoadContext(TestConfig config)
    {
        using Target target = await Targets.GetTargetAsync(config);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        // AssemblyLoadContext is a .NET Core concept; the debuggee loads into the default ALC.
        target.Sos($"dumpalc {target.FindUniqueObject("FieldMarker"):x}").AssertContains("DefaultAssemblyLoadContext");
    }

    [MatrixTheory]
    [MemberData(nameof(Matrix))]
    public async Task GcHandleLeaks_RunsHandleScan(TestConfig config)
    {
        using Target target = await Targets.GetTargetAsync(config);
        target.GoToStopPoint(TargetCatalog.StopHeap);

        KnownIssues.SkipGcHandleLeaksOffWindows();

        target.Sos("gchandleleaks").AssertContains("GCHandle");
    }
}
