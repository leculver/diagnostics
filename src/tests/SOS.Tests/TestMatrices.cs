// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SOS.TestHarness;
using Xunit;

namespace SOS.Tests;

internal static class TestMatrices
{
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
