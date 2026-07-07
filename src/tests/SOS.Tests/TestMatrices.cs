// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SOS.TestHarness;
using Xunit;

namespace SOS.Tests;

internal static class TestMatrices
{
    public static TheoryData<TestConfig> CoreFrameworkFullDump(string[] targets)
    {
        TheoryData<TestConfig> data = new();
        foreach (TestConfig config in CoreFrameworkFullDumpConfigs(targets))
        {
            data.Add(config);
        }

        return data;
    }

    public static IEnumerable<TestConfig> CoreFrameworkFullDumpConfigs(string[] targets) =>
        TestConfig.Permutations(targets, flavor: Flavor.Core | Flavor.Framework, dumpKind: DumpKind.Full);
}
