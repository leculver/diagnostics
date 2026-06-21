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
    private static readonly ConcurrentDictionary<(Host Host, string Target, string Stop, Flavor Flavor, bool PublicSymbols), Lazy<DumpSession>> s_sessions = new();
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
    public static Task<Target> GetTargetAsync(string target, Host host, Flavor flavor, Liveness liveness, bool publicSymbols = false)
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
            if (publicSymbols)
            {
                throw new ArgumentException("publicSymbols is only supported for dump (dead) targets.", nameof(publicSymbols));
            }

            return Task.Run<Target>(() => {
                string exe = SnapshotStore.TargetExe(flavor, target);
                return new LiveTarget(host, definition, flavor, exe);
            });
        }

        // Dead targets are cheap cursors; the heavy work (capture + load) happens on first GoTo,
        // memoized per point so parallel tests navigating to the same point share one dump host.
        return Task.FromResult<Target>(new DeadTarget(host, target, flavor, publicSymbols));
    }

    /// <summary>
    /// Resolve (memoized, process-wide) the read-only dump session for one point — produced and SOS
    /// loaded on first use, then reused by every <see cref="DeadTarget"/> that navigates here.
    /// </summary>
    internal static DumpSession ResolveSession(Host host, string target, Flavor flavor, string stop, bool publicSymbols = false)
    {
        return s_sessions
            .GetOrAdd((host, target, stop, flavor, publicSymbols), key => new Lazy<DumpSession>(() => CreateSession(key)))
            .Value;
    }

    private static DumpSession CreateSession((Host Host, string Target, string Stop, Flavor Flavor, bool PublicSymbols) key)
    {
        string dump = SnapshotStore.GetDump(key.Flavor, key.Target, key.Stop);
        DumpSession session = new(key.Host, key.Target, key.Stop, key.Flavor, dump, key.PublicSymbols);
        s_created.Add(session);
        return session;
    }

    public static TheoryData<Host, Flavor, Liveness> BuildMatrix(Flavor flavor = Flavor.AllValid, Host host = Host.AllValid, Liveness liveness = Liveness.AllValid)
    {
        TheoryData<Host, Flavor, Liveness> theoryData = new();

        foreach (Host h in SingleFlags(host, "SOSHARNESS_ONLY_HOSTS"))
        {
            // Platform constraint: cdb is Windows-only, lldb is non-Windows-only.
            if (h == Host.Cdb && !OperatingSystem.IsWindows())
            {
                continue;
            }

            if (h == Host.Lldb && OperatingSystem.IsWindows())
            {
                continue;
            }

            foreach (Flavor f in SingleFlags(flavor, "SOSHARNESS_ONLY_FLAVORS"))
            {
                // Framework is Windows-only.
                if (f == Flavor.Framework && !OperatingSystem.IsWindows())
                {
                    continue;
                }

                foreach (Liveness l in SingleFlags(liveness, "SOSHARNESS_ONLY_LIVENESS"))
                {
                    // Live + DotnetDump is not a valid combination.
                    if (l == Liveness.Live && h == Host.DotnetDump)
                    {
                        continue;
                    }

                    theoryData.Add(h, f, l);
                }
            }
        }

        return theoryData;
    }


    public static TheoryData<string, Host, Flavor, Liveness> BuildMatrix(string[] targets, Flavor flavor = Flavor.AllValid, Host host = Host.AllValid, Liveness liveness = Liveness.AllValid)
    {
        TheoryData<string, Host, Flavor, Liveness> theoryData = new();

        foreach (Host h in SingleFlags(host, "SOSHARNESS_ONLY_HOSTS"))
        {
            // Platform constraint: cdb is Windows-only, lldb is non-Windows-only.
            if (h == Host.Cdb && !OperatingSystem.IsWindows())
            {
                continue;
            }

            if (h == Host.Lldb && OperatingSystem.IsWindows())
            {
                continue;
            }

            foreach (Flavor f in SingleFlags(flavor, "SOSHARNESS_ONLY_FLAVORS"))
            {
                // Framework is Windows-only.
                if (f == Flavor.Framework && !OperatingSystem.IsWindows())
                {
                    continue;
                }

                foreach (Liveness l in SingleFlags(liveness, "SOSHARNESS_ONLY_LIVENESS"))
                {
                    // Live + DotnetDump is not a valid combination.
                    if (l == Liveness.Live && h == Host.DotnetDump)
                    {
                        continue;
                    }

                    foreach (string target in targets)
                    {
                        // Skip flavors a target can't support (e.g. DynamicMethod can't build for desktop).
                        if ((TargetCatalog.FlavorsFor(target) & f) == 0)
                        {
                            continue;
                        }

                        theoryData.Add(target, h, f, l);
                    }
                }
            }
        }

        return theoryData;
    }

    private static IEnumerable<T> SingleFlags<T>(T value) where T : struct, Enum
    {
        foreach (T candidate in Enum.GetValues<T>())
        {
            long v = Convert.ToInt64(candidate);
            if (v != 0 && (v & (v - 1)) == 0 && (Convert.ToInt64(value) & v) != 0)
            {
                yield return candidate;
            }
        }
    }

    /// <summary>
    /// Like <see cref="SingleFlags{T}(T)"/>, but additionally narrowed by an optional comma-separated
    /// allow-list in <paramref name="envVar"/> (enum names, case-insensitive). Lets a run be staged onto
    /// a subset of the matrix during bring-up, e.g. <c>SOSHARNESS_ONLY_FLAVORS=Core</c>,
    /// <c>SOSHARNESS_ONLY_HOSTS=Cdb,DotnetDump</c>, <c>SOSHARNESS_ONLY_LIVENESS=Dump</c>.
    /// </summary>
    private static IEnumerable<T> SingleFlags<T>(T value, string envVar) where T : struct, Enum
    {
        string? only = Environment.GetEnvironmentVariable(envVar);
        HashSet<string>? allowed = string.IsNullOrEmpty(only)
            ? null
            : new HashSet<string>(only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);

        foreach (T candidate in SingleFlags(value))
        {
            if (allowed is null || allowed.Contains(candidate.ToString()))
            {
                yield return candidate;
            }
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
