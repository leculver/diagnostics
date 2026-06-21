// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace SOS.TestHarness;

/// <summary>Creates the concrete host for a given host kind.</summary>
internal static class HostFactory
{
    /// <summary>A dump-backed host (shared, read-only). <paramref name="publicSymbols"/> opts a cdb host
    /// into the sealed public-msdl symbol path for OS-symbol-dependent commands (e.g. <c>!maddress</c>).</summary>
    public static IDebuggerHost CreateDumpHost(Host host, Flavor flavor, string dumpPath, bool publicSymbols = false) => host switch
    {
        // cdb runs dbgeng in a CHILD process (EngineHost), so the test host never loads dbgeng.
        Host.Cdb => ChildEngineClient.ForDump(host.ToString().ToLowerInvariant(), dumpPath, DacDirFor(flavor), publicSymbols),
        Host.DotnetDump => new DotNetDumpHost(dumpPath),
        Host.Lldb => throw new NotSupportedException("lldb host is not implemented in this PoC."),
        _ => throw new ArgumentException($"Unknown host '{host}'."),
    };

    /// <summary>A live host (exclusive, advancing) — also a child EngineHost process.</summary>
    public static ChildEngineClient CreateLiveHost(Host host, Flavor flavor, string exePath) => host switch
    {
        Host.Cdb => ChildEngineClient.ForLive(host.ToString().ToLowerInvariant(), exePath, DacDirFor(flavor)),
        Host.Lldb => throw new NotSupportedException("lldb live host is not implemented in this PoC."),
        Host.DotnetDump => throw new ArgumentException("dotnet-dump is post-mortem only; it has no live host."),
        _ => throw new ArgumentException($"Unknown live host '{host}'."),
    };

    /// <summary>
    /// The DAC directory to make dbgeng load explicitly for a flavor. Self-contained single-file bundles
    /// the runtime, so cdb can't find <c>mscordaccore.dll</c> on disk — point it at the runtime pack's
    /// DAC. Other flavors find their DAC next to the runtime, so they need no override.
    /// </summary>
    private static string? DacDirFor(Flavor flavor) =>
        flavor == Flavor.SingleFile ? ToolPaths.SingleFileDacDirectory : null;
}
