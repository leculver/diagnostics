// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace SOS.TestHarness;

/// <summary>
/// A live, advancing target owned exclusively by one test. The debuggee is launched under a child
/// <see cref="ChildEngineClient"/> (its own EngineHost process) and parked at the debugger's initial
/// breakpoint - before CoreCLR loads - with SOS loaded. <see cref="Sos"/> already works there (e.g.
/// <c>bpmd</c>, which sets a pending managed breakpoint), so it does <em>not</em> throw before the
/// first navigation, unlike a <see cref="DeadTarget"/>.
///
/// Navigation only moves forward and tracks where we are stopped: re-asking for the current point is
/// a no-op; asking for a later one runs there (skipping intervening stop points); if the process
/// crashes or exits before reaching it, an <see cref="InvalidOperationException"/> is thrown.
/// <see cref="RunToBpmd"/> is the raw form for breaking on an arbitrary method. Dispose (use
/// <c>using</c>) to shut the child down.
/// </summary>
public sealed class LiveTarget : Target
{
    // Sentinel for "stopped at the crash" - not a real stop-point name (can't collide).
    private const string CrashMarker = "\0crash";

    private readonly TargetDefinition _definition;
    private ChildEngineClient? _host;
    private string? _at; // current stop name, CrashMarker, or null (still at the initial break)
    private bool _disposed;

    internal LiveTarget(Host hostKind, TargetDefinition definition, Flavor flavor, string exePath)
        : base(hostKind, definition.Name, flavor)
    {
        _definition = definition;
        _host = HostFactory.CreateLiveHost(hostKind, exePath);
    }

    protected override void GoToStopPointCore(string stopName)
    {
        StopPoint stop = _definition.Stop(stopName);
        if (stop.Method is null)
        {
            throw new InvalidOperationException(
                $"Stop point '{stopName}' has no method to break on (kind {stop.Kind}); use GoToCrash().");
        }

        if (_at == stop.Name)
        {
            return; // already here
        }

        // Runs forward to the marker; throws if the process exits/crashes before reaching it. The
        // managed module for bpmd is flavor-specific (desktop's is the EXE, .NET Core's the DLL).
        Engine.RunToBpmd(_definition.ModuleFor(Flavor), stop.Method);
        _at = stop.Name;
    }

    protected override void GoToCrashCore()
    {
        if (_at == CrashMarker)
        {
            return; // already at the crash; repeatable no-op
        }

        Engine.RunToCrash(); // throws if the process exits without crashing
        _at = CrashMarker;
    }

    /// <summary>
    /// Resume the live process until it next hits a breakpoint. Unlike <see cref="GoToStopPoint"/>
    /// this sets and clears nothing — the caller arms the breakpoint (e.g.
    /// <c>Sos("bpmd Module Method")</c>) and this just runs to it. Throws if the process exits first.
    /// </summary>
    public void RunToBreakpoint()
    {
        Engine.RunToBreakpoint();
        _at = null; // arbitrary, caller-managed location — not a named point
        ReplayContext.Current?.Add(ReplayStepKind.Navigate, "RunToBreakpoint()", null);
    }

    protected override SosOutput SosCore(string command) => Engine.Sos(command);

    protected override SosOutput ExecuteCore(string command) => Engine.Execute(command);

    private ChildEngineClient Engine =>
        _host ?? throw new ObjectDisposedException(nameof(LiveTarget));

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host?.Dispose();
        _host = null;
    }
}
