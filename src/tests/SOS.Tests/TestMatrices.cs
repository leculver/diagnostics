// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SOS.TestHarness;
using Xunit;

namespace SOS.Tests;

internal static class TestMatrices
{
    /// <summary>
    /// Wraps <see cref="TestConfig.BuildMatrix"/> for commands whose data is absent from a reduced Heap dump on
    /// some .NET versions but present on others, capturing a Full dump for the versions named in
    /// <paramref name="fullDumpVersions"/> and staying on the default Heap dump for the rest. Reduced Heap dumps
    /// on the affected versions omit per-method debug info the DAC needs — JIT variable/argument info for
    /// <c>clrstack -p/-l/-a</c>; the method debug data behind <c>!ehinfo</c>, <c>!ip2md</c> source lines,
    /// <c>!clru</c> IL interleaving, gcroot pinned-root reporting, native/managed frame annotation for
    /// <c>!dumpstack</c>/<c>!eestack</c>; or the ThreadPool state behind <c>!threadpool</c>. The runtime later
    /// began including that info in Heap dumps (net8-net10 gaps closed by net11; the <c>!threadpool</c> gap
    /// closed by net9), and desktop Framework already carries it. Both legacy and cDAC read the data fine once
    /// it is present, so this is purely a capture-side (dump contents) workaround. Takes the same axes as
    /// <see cref="TestConfig.BuildMatrix"/> so it is a drop-in replacement.
    ///
    /// <para>Only applied on Windows: Linux ELF core Heap dumps already include this debug info, so those configs
    /// stay on the default Heap dump.</para>
    /// </summary>
    public static TheoryData<TestConfig> FullDumpOnCoreVersions(
        string[] targets,
        CoreVersion fullDumpVersions,
        Flavor flavor = Flavor.AllValid,
        Host host = Host.AllValid,
        Liveness liveness = Liveness.Dump,
        GcType gcType = GcType.Workstation,
        DumpKind dumpKind = DumpKind.Heap,
        bool publicSymbols = false,
        CoreVersion coreVersion = CoreVersion.All,
        Dac dac = Dac.All)
    {
        TheoryData<TestConfig> data = new();
        foreach (TestConfig config in TestConfig.Permutations(targets, flavor, host, liveness, gcType, dumpKind, publicSymbols, coreVersion, dac))
        {
            if (OperatingSystem.IsWindows() && (config.CoreVersion & fullDumpVersions) != 0)
            {
                data.Add(config with { DumpKind = DumpKind.Full });
            }
            else
            {
                data.Add(config);
            }
        }

        return data;
    }

    public static TheoryData<TestConfig> CoreFrameworkConditional(string[] targets)
    {
        TheoryData<TestConfig> data = new();
        foreach (TestConfig config in CoreFrameworkConditionalFullDumpConfigs(targets))
        {
            data.Add(config);
        }

        return data;
    }

    public static IEnumerable<TestConfig> CoreFrameworkConditionalFullDumpConfigs(string[] targets)
    {
        foreach (TestConfig config in TestConfig.Permutations(targets, flavor: Flavor.Core | Flavor.Framework, dumpKind: DumpKind.Heap))
        {
            if (!OperatingSystem.IsWindows() && config.CoreVersion == CoreVersion.Net10)
            {
                // The net10 legacy DAC can crash while servicing dumpobj's optional ComWrappers data query on reduced Heap dumps.
                yield return config with { DumpKind = DumpKind.Full };
            }
            else
            {
                yield return config;
            }
        }
    }
}
