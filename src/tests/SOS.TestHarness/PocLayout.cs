// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace SOS.TestHarness;

/// <summary>
/// Locates the PoC root (the folder containing <c>sos-harness.sln</c>) by walking up from
/// the test output directory, so the harness can find the debuggee project and dump folder
/// regardless of where it is run from.
/// </summary>
public static class PocLayout
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "sos-harness.sln")) ||
                File.Exists(Path.Combine(dir, "sos-harness.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            "Could not locate the PoC root (sos-harness.sln/.slnx) by walking up from " + AppContext.BaseDirectory);
    }
}
