// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SOS.TestHarness;
using Xunit;

namespace SOS.Tests;

/// <summary>
/// Coverage for <c>!clrstack -f</c> (full / native-interleaved stack). The legacy scripts ran <c>-f</c>
/// on WebApp/StackTests but only shape-checked it. Here the oracle is cross-variant: every managed
/// frame from plain <c>clrstack</c> is preserved in <c>-f</c> (matched by IP) and rendered in the full
/// assembly-qualified <c>Assembly.dll!Method + offset</c> format. The native dimension is host-specific
/// (see issues.md#clrstack-f-dotnet-dump-no-native-frames): under cdb, <c>-f</c> must be strictly
/// larger than plain and contain real native runtime frames (coreclr!/clr!/ntdll!/…); under the
/// managed-only dotnet-dump host it contains no native frames.
/// </summary>
public sealed class ClrStackFullTests
{
    public static TheoryData<string, Host, Flavor, Liveness> Matrix { get; }
        = Targets.BuildMatrix(
            [
                TargetCatalog.SimpleThrow,
                TargetCatalog.DivZero,
                TargetCatalog.NestedException,
            ]);

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task ClrStack_Full(string targetName, Host host, Flavor flavor, Liveness liveness)
    {
        using Target target = await Targets.GetTargetAsync(targetName, host, flavor, liveness);
        target.GoToFirstStop();

        SosTable plain = target.Clrstack();
        IReadOnlyList<TargetExtensions.FullFrame> full = target.ClrstackFull();

        // The IPs of the managed (non-internal) frames in plain clrstack.
        HashSet<string> plainManagedIps = plain
            .Where(r => !r["InternalFrame"].AsBoolean())
            .Select(r => r["IP"].Value.ToUpperInvariant())
            .ToHashSet();
        Assert.NotEmpty(plainManagedIps);

        HashSet<string> fullIps = full
            .Where(f => f.IP.Length > 0)
            .Select(f => f.IP.ToUpperInvariant())
            .ToHashSet();

        // Every managed frame from plain clrstack is preserved in -f.
        foreach (string ip in plainManagedIps)
            Assert.Contains(ip, fullIps);

        // -f renders managed frames assembly-qualified (Assembly.dll!Method + offset).
        Assert.Contains(full, f => f.IsManaged);

        bool nativeHost = host == Host.Cdb || host == Host.Lldb;
        if (nativeHost)
        {
            // Native interleaving: strictly more frames than plain, with real native runtime frames.
            Assert.True(full.Count > plain.Length,
                $"-f ({full.Count}) should have more frames than plain clrstack ({plain.Length}) under a native host.");
            Assert.Contains(full, f => f.IsNativeRuntime);
        }
        else
        {
            // dotnet-dump is managed-only: no native runtime frames (see issues.md).
            Assert.DoesNotContain(full, f => f.IsNativeRuntime);
        }
    }
}
