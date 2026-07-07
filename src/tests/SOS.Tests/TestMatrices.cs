// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SOS.TestHarness;
using Xunit;

namespace SOS.Tests;

internal static class TestMatrices
{
    /// <summary>
    /// Matrix for commands that read JIT'd method locals/parameters (e.g. <c>clrstack -p/-l/-a</c>). On
    /// .NET 8–10 the reduced Heap dump does not carry the JIT variable/argument debug info that the DAC
    /// needs to map locals and value-type parameters to their stack slots, so those variables come back
    /// empty (only reference-typed args that live on the GC heap resolve). The runtime includes that debug
    /// info in Heap dumps starting with .NET 11, so we capture a Full dump on net8–net10 and stay on the
    /// default Heap dump everywhere else. Both legacy and cDAC read the info fine once it is present in the
    /// dump, so this is purely a capture-side (dump contents) workaround, not a DAC difference.
    /// </summary>
    public static TheoryData<TestConfig> FullDumpForLocalsBeforeNet11(string[] targets)
    {
        TheoryData<TestConfig> data = new();
        foreach (TestConfig config in TestConfig.Permutations(targets))
        {
            if (config.CoreVersion is CoreVersion.Net8 or CoreVersion.Net9 or CoreVersion.Net10)
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
