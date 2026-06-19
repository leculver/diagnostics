// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace SOS.TestHarness;

/// <summary>Creates the concrete host for a given host kind.</summary>
internal static class HostFactory
{
    /// <summary>A dump-backed host (shared, read-only).</summary>
    public static IDebuggerHost CreateDumpHost(Host host, string dumpPath) => host switch
    {
        // cdb runs dbgeng in a CHILD process (EngineHost), so the test host never loads dbgeng.
        Host.Cdb => ChildEngineClient.ForDump(host.ToString().ToLowerInvariant(), dumpPath),
        Host.DotnetDump => new DotNetDumpHost(dumpPath),
        Host.Lldb => throw new NotSupportedException("lldb host is not implemented in this PoC."),
        _ => throw new ArgumentException($"Unknown host '{host}'."),
    };

    /// <summary>A live host (exclusive, advancing) — also a child EngineHost process.</summary>
    public static ChildEngineClient CreateLiveHost(Host host, string exePath) => host switch
    {
        Host.Cdb => ChildEngineClient.ForLive(host.ToString().ToLowerInvariant(), exePath),
        Host.Lldb => throw new NotSupportedException("lldb live host is not implemented in this PoC."),
        Host.DotnetDump => throw new ArgumentException("dotnet-dump is post-mortem only; it has no live host."),
        _ => throw new ArgumentException($"Unknown live host '{host}'."),
    };
}
