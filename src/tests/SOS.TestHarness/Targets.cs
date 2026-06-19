// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Xunit;

namespace SOS.TestHarness;

/// <summary>
/// The single entry point tests use to get a debug target. Shared (dump-backed) targets are
/// memoized process-wide by <c>(host, target, stopPoint)</c> so the expensive load happens once
/// and is reused across every test that asks for the same triple. Live targets are exclusive and
/// never memoized — each call hands the caller its own advancing debuggee.
///
/// Shared hosts (notably the dotnet-dump child processes) must be torn down at the end of the run,
/// or their lingering child processes keep the test host alive. <see cref="DisposeAll"/> does this;
/// it is wired to <see cref="AppDomain.ProcessExit"/> and also exposed for an explicit assembly
/// teardown fixture.
/// </summary>
public static class Targets
{
    private static readonly ConcurrentDictionary<(Host Host, string Target, string Stop, Flavor Flavor), Lazy<DumpSession>> s_sessions = new();
    private static readonly ConcurrentBag<DumpSession> s_created = new();

    static Targets()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposeAll();
    }

    /// <summary>
    /// Get a debug target for <paramref name="target"/> built as <paramref name="flavor"/> under
    /// <paramref name="host"/>. With <paramref name="liveness"/> = <see cref="Liveness.Dump"/> you get a
    /// dump-backed <see cref="DeadTarget"/> (navigate to cached dumps in any order); with
    /// <see cref="Liveness.Live"/> you get a <see cref="LiveTarget"/> launched and parked at the
    /// debugger's initial breakpoint. Awaiting is the "gate" — for live it completes once the process
    /// is launched and SOS is ready. Pass exactly one of <see cref="Liveness.Live"/> /
    /// <see cref="Liveness.Dump"/> (the per-case value a theory receives); the combined
    /// <see cref="Liveness.AllValid"/> is a matrix selector, not a single target, and throws.
    /// </summary>
    public static Task<Target> GetTargetAsync(string target, Host host, Flavor flavor, Liveness liveness)
    {
        bool live = liveness switch
        {
            Liveness.Live => true,
            Liveness.Dump => false,
            _ => throw new ArgumentOutOfRangeException(
                nameof(liveness), liveness, "Expected exactly Liveness.Live or Liveness.Dump."),
        };

        TargetDefinition definition = TargetCatalog.Get(target);

        // Begin capturing this test's replay timeline (host/flavor/liveness + every command/dump).
        ReplayContext.Start(target, host, flavor, live);

        if (live)
        {
            return Task.Run<Target>(() =>
            {
                string exe = SnapshotStore.TargetExe(flavor, target);
                return new LiveTarget(host, definition, flavor, exe);
            });
        }

        // Dead targets are cheap cursors; the heavy work (capture + load) happens on first GoTo,
        // memoized per point so parallel tests navigating to the same point share one dump host.
        return Task.FromResult<Target>(new DeadTarget(host, target, flavor));
    }

    /// <summary>
    /// Resolve (memoized, process-wide) the read-only dump session for one point — produced and SOS
    /// loaded on first use, then reused by every <see cref="DeadTarget"/> that navigates here.
    /// </summary>
    internal static DumpSession ResolveSession(Host host, string target, Flavor flavor, string stop)
    {
        return s_sessions
            .GetOrAdd((host, target, stop, flavor), key => new Lazy<DumpSession>(() => CreateSession(key)))
            .Value;
    }

    private static DumpSession CreateSession((Host Host, string Target, string Stop, Flavor Flavor) key)
    {
        string dump = SnapshotStore.GetDump(key.Flavor, key.Target, key.Stop);
        DumpSession session = new(key.Host, key.Target, key.Stop, key.Flavor, dump);
        s_created.Add(session);
        return session;
    }

    public static TheoryData<Host, Flavor, Liveness> BuildMatrix(Flavor flavor = Flavor.AllValid, Host host = Host.AllValid, Liveness liveness = Liveness.AllValid)
    {
        var theoryData = new TheoryData<Host, Flavor, Liveness>();

        foreach (var h in SingleFlags(host))
        {
            // Platform constraint: cdb is Windows-only, lldb is non-Windows-only.
            if (h == Host.Cdb && !OperatingSystem.IsWindows())
                continue;

            if (h == Host.Lldb && OperatingSystem.IsWindows())
                continue;

            foreach (var f in SingleFlags(flavor))
            {
                // Framework is Windows-only.
                if (f == Flavor.Framework && !OperatingSystem.IsWindows())
                    continue;

                foreach (var l in SingleFlags(liveness))
                {
                    // Live + DotnetDump is not a valid combination.
                    if (l == Liveness.Live && h == Host.DotnetDump) continue;

                    theoryData.Add(h, f, l);
                }
            }
        }

        return theoryData;
    }


    public static TheoryData<string, Host, Flavor, Liveness> BuildMatrix(string[] targets, Flavor flavor = Flavor.AllValid, Host host = Host.AllValid, Liveness liveness = Liveness.AllValid)
    {
        var theoryData = new TheoryData<string, Host, Flavor, Liveness>();

        foreach (var h in SingleFlags(host))
        {
            // Platform constraint: cdb is Windows-only, lldb is non-Windows-only.
            if (h == Host.Cdb && !OperatingSystem.IsWindows())
                continue;

            if (h == Host.Lldb && OperatingSystem.IsWindows())
                continue;

            foreach (var f in SingleFlags(flavor))
            {
                // Framework is Windows-only.
                if (f == Flavor.Framework && !OperatingSystem.IsWindows())
                    continue;

                foreach (var l in SingleFlags(liveness))
                {
                    // Live + DotnetDump is not a valid combination.
                    if (l == Liveness.Live && h == Host.DotnetDump)
                        continue;

                    foreach (string target in targets)
                    {
                        theoryData.Add(target, h, f, l);
                    }
                }
            }
        }

        return theoryData;
    }

    private static IEnumerable<T> SingleFlags<T>(T value) where T : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<T>())
        {
            long v = Convert.ToInt64(candidate);
            if (v != 0 && (v & (v - 1)) == 0 && (Convert.ToInt64(value) & v) != 0)
                yield return candidate;
        }
    }

    /// <summary>Dispose every memoized dump session (kills dotnet-dump children, closes dbgeng hosts).</summary>
    public static void DisposeAll()
    {
        while (s_created.TryTake(out DumpSession? session))
        {
            try
            {
                session.Dispose();
            }
            catch
            {
                // best effort teardown
            }
        }

        // Close any pooled (dotnet-dump) host still open. cdb children were disposed above via
        // each SharedTarget.Dispose().
        HostSlot.DotNetDump.CloseCurrent();
    }
}
