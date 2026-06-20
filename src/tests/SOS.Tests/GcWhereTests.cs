// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SOS.TestHarness;
using Xunit;

namespace SOS.Tests;

/// <summary>
/// Snapshot multi-stop tests across the full host × flavor × stopPoint matrix. One debuggee run
/// per (flavor, target) produces gen0/gen1/gen2 dumps; each is an immutable, independently-loaded
/// stop point. ClrMD is the oracle: it finds the array and its generation, and we assert SOS's
/// <c>gcwhere</c> agrees — for .NET Core, single-file, and desktop .NET Framework alike.
/// </summary>
public sealed class GcWhereTests
{
    public static TheoryData<Host, Flavor, Liveness> Matrix => Targets.BuildMatrix();
    public static TheoryData<Host, Flavor, Liveness> StructureMatrix => Targets.BuildMatrix(Flavor.AllValid, Host.AllValid, Liveness.Dump);

    [Theory]
    [MemberData(nameof(StructureMatrix))]
    public async Task GcWhere_Structure(Host host, Flavor flavor, Liveness liveness)
    {
        using Target target = await Targets.GetTargetAsync(TargetCatalog.Scenarios, host, flavor, liveness);
        target.GoToStopPoint("gen0");

        ulong obj = FindObject(target);
        SosOutput gcwhere = target.Sos($"gcwhere {obj:x}");

        SosTable table = gcwhere.Table(
            ("Address", Sos.Addr), ("Heap", Sos.Integer), ("Segment", Sos.Addr), ("Generation", Sos.Integer),
            ("Allocated", Sos.MemRange), ("Committed", Sos.MemRange), ("Reserved", Sos.MemRange));
        Assert.NotEmpty(table);
    }

    private static ulong FindObject(Target target)
    {
        SosTable dsoTable = target.DumpStackObjects();
        SosRow row = dsoTable.First(r => r["Name"] == "System.Int32[]");
        ulong obj = row["Object"].AsUInt64(Sos.Addr);
        return obj;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task GcWhere_Moves(Host host, Flavor flavor, Liveness liveness)
    {
        KnownIssues.SkipLiveGenPromotion(liveness);
        using Target target = await Targets.GetTargetAsync(TargetCatalog.Scenarios, host, flavor, liveness);

        CheckGeneration(target, 0);
        CheckGeneration(target, 1);
        CheckGeneration(target, 2);
    }

    private static void CheckGeneration(Target target, int gen)
    {
        target.GoToStopPoint($"gen{gen}");
        ulong obj = FindObject(target);
        SosOutput gcwhere = target.Sos($"gcwhere {obj:x}");

        SosTable table = gcwhere.Table("Address", "Heap", "Segment", "Generation", "Allocated", "Committed", "Reserved");
        SosRow row = table.SingleRow(r => r["Address"].AsUInt64(Sos.Addr) == obj, $"a row whose Address is 0x{obj:x}");
        Assert.Equal(gen, row["Generation"].AsInt32(Sos.Integer));
    }
}
