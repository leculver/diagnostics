// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace SOS.TestHarness;

/// <summary>
/// An internal, read-only debug session over a specific dump (one <c>(target, stopPoint, flavor)</c>)
/// loaded into a host, with SOS ready. Because the dump is immutable, a session is safe to reuse
/// across many assertions, and is memoized by <see cref="Targets"/> and reused by every
/// <see cref="DeadTarget"/> cursor that navigates to the same point.
///
/// Host lifetime differs by backend:
/// <list type="bullet">
///   <item><b>cdb</b> runs dbgeng in its own <see cref="ChildEngineClient"/> child process.
///   Each child is independent and blocks on stdin when idle, so many can be alive at once — the
///   single-instance limit that in-process dbgeng imposed is gone. The host is created once and
///   kept. Because one session may be reused by several tests at once, concurrent commands
///   on its single child are serialized on a per-session gate.</item>
///   <item><b>dotnet-dump</b> children busy-wait on stdin at ~100% CPU, so keeping many alive would
///   saturate the machine. They route through a capacity-1 <see cref="HostSlot"/> (most-recently-used
///   stays open, reopened on demand).</item>
/// </list>
/// </summary>
internal sealed class DumpSession : IPooledHost, IDisposable
{
    private readonly Host _hostKind;
    private readonly bool _pooled;       // dotnet-dump: route through the single slot
    private readonly HostSlot? _slot;
    private readonly object _gate = new(); // serializes concurrent commands on this shared child
    private readonly bool _publicSymbols;  // cdb: use the sealed public-msdl symbol path (OS-symbol tests)
    private IDebuggerHost? _host;        // kept-alive host for non-pooled (cdb child) targets

    public Host Host { get; }
    public string TargetName { get; }
    public string StopName { get; }
    public Flavor Flavor { get; }
    public string DumpPath { get; }
    public CoreVersion CoreVersion { get; }
    public Dac Dac { get; }

    internal DumpSession(Host hostKind, string targetName, string stopName, Flavor flavor, string dumpPath, bool publicSymbols = false,
                         CoreVersion coreVersion = CoreVersion.Net10, Dac dac = Dac.Legacy)
    {
        _hostKind = hostKind;
        Host = hostKind;
        TargetName = targetName;
        StopName = stopName;
        Flavor = flavor;
        DumpPath = dumpPath;
        _publicSymbols = publicSymbols;
        CoreVersion = coreVersion;
        Dac = dac;

        // dotnet-dump children spin on stdin -> bound to one via the slot. cdb children block
        // when idle -> keep alive concurrently (no slot), which is the subprocess-backend payoff.
        _pooled = hostKind == Host.DotnetDump;
        _slot = _pooled ? HostSlot.DotNetDump : null;

        if (!_pooled)
        {
            _host = HostFactory.CreateDumpHost(hostKind, flavor, dumpPath, _publicSymbols, dac, coreVersion,
                SnapshotStore.TargetExe(flavor, targetName, coreVersion));
            _host.LoadSos();
        }
    }

    /// <summary>
    /// Run a SOS command against this target (host prefixing handled by the host). A shared target
    /// may be handed to several tests at once (it is memoized by host/target/stop/flavor), and the
    /// cdb backend is a single child process whose stdin/stdout pipe is not safe for concurrent
    /// callers — so non-pooled commands are serialized on a per-target gate. The dotnet-dump path
    /// serializes itself on the slot lock.
    /// </summary>
    public SosOutput Sos(string command) =>
        _pooled ? _slot!.Run(this, h => h.Sos(command)) : RunGuarded(h => h.Sos(command));

    /// <summary>Run a raw debugger command against this target.</summary>
    public SosOutput Execute(string command) =>
        _pooled ? _slot!.Run(this, h => h.Execute(command)) : RunGuarded(h => h.Execute(command));

    private SosOutput RunGuarded(Func<IDebuggerHost, SosOutput> action)
    {
        lock (_gate)
        {
            return action(_host!);
        }
    }

    // IPooledHost — used only for the pooled (dotnet-dump) path.

    IDebuggerHost IPooledHost.Host => _host!;

    void IPooledHost.OpenHost()
    {
        _host = HostFactory.CreateDumpHost(_hostKind, Flavor, DumpPath, _publicSymbols, Dac, CoreVersion,
            SnapshotStore.TargetExe(Flavor, TargetName, CoreVersion));
        _host.LoadSos();
    }

    void IPooledHost.CloseHost()
    {
        _host?.Dispose();
        _host = null;
    }

    public void Dispose()
    {
        if (_pooled)
        {
            // The slot owns the pooled host's lifetime; closed at teardown via the slot.
            return;
        }

        _host?.Dispose();
        _host = null;
    }
}
