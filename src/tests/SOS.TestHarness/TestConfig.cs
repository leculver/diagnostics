// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using Xunit.Sdk;

namespace SOS.TestHarness;

/// <summary>
/// One row of the test matrix: the full set of axes that define a single debug-target configuration a
/// theory runs against. Replaces the old positional <c>(Host, Flavor, Liveness)</c> tuple so adding axes
/// (GC type, dump kind, ...) doesn't widen every signature. A test takes a single <see cref="TestConfig"/>
/// parameter and hands it straight to <see cref="Targets.GetTargetAsync(TestConfig)"/>.
///
/// <para>Implements <see cref="IXunitSerializable"/> so each row has a stable, individually-runnable test
/// id and a legible display name (see <see cref="ToString"/>), and has value equality so
/// <see cref="BuildMatrix"/> can de-duplicate rows that collapse onto the same configuration.</para>
/// </summary>
public sealed class TestConfig : IXunitSerializable, IEquatable<TestConfig>
{
    /// <summary>The debuggee target name (e.g. <see cref="TargetCatalog.Scenarios"/>).</summary>
    public string Target { get; private set; } = string.Empty;

    /// <summary>The debugger host (cdb / dotnet-dump / lldb).</summary>
    public Host Host { get; private set; }

    /// <summary>The runtime flavor (Core / SingleFile / Framework).</summary>
    public Flavor Flavor { get; private set; }

    /// <summary>Live process vs. post-mortem dump.</summary>
    public Liveness Liveness { get; private set; }

    /// <summary>Workstation vs. server GC.</summary>
    public GcType GcType { get; private set; }

    /// <summary>The dump kind (Full / Mini). Always <see cref="DumpKind.Full"/> for live targets (no dump).</summary>
    public DumpKind DumpKind { get; private set; }

    /// <summary>
    /// Opt the (cdb) dump host into the sealed public-msdl symbol path for OS-symbol-dependent commands
    /// (e.g. <c>!maddress</c>). Dump-only; never set for live targets.
    /// </summary>
    public bool PublicSymbols { get; private set; }

    /// <summary>Parameterless ctor required by <see cref="IXunitSerializable"/>; do not use directly.</summary>
    public TestConfig()
    {
    }

    public TestConfig(string target, Host host, Flavor flavor, Liveness liveness,
                      GcType gcType = GcType.Workstation, DumpKind dumpKind = DumpKind.Full, bool publicSymbols = false)
    {
        Target = target;
        Host = host;
        Flavor = flavor;
        Liveness = liveness;
        GcType = gcType;
        DumpKind = dumpKind;
        PublicSymbols = publicSymbols;
    }

    /// <summary>True for a live process target; false for a post-mortem dump.</summary>
    public bool IsLive => Liveness == Liveness.Live;

    /// <summary>Return a copy with <see cref="PublicSymbols"/> set (for the OS-symbol Facts).</summary>
    public TestConfig WithPublicSymbols(bool value = true) =>
        new(Target, Host, Flavor, Liveness, GcType, DumpKind, value);

    /// <summary>
    /// Generate the cross-product of the requested axes as a single-column theory source, filtered to the
    /// valid configurations for the current platform (see <see cref="IsValid"/>).
    ///
    /// <para>Axis defaults are deliberate: <paramref name="host"/>/<paramref name="flavor"/>/
    /// <paramref name="liveness"/> default to <c>AllValid</c> (full coverage), but <paramref name="gcType"/>
    /// defaults to <see cref="GcType.Workstation"/> and <paramref name="dumpKind"/> to
    /// <see cref="DumpKind.Full"/> — Server GC and Mini dumps are opt-in, so the matrix doesn't explode and
    /// reduced-dump-only failures aren't swept into every test.</para>
    ///
    /// <para>Each axis can be narrowed at run time by a comma-separated env allow-list:
    /// <c>SOSHARNESS_ONLY_HOSTS</c>, <c>_FLAVORS</c>, <c>_LIVENESS</c>, <c>_GCTYPE</c>, <c>_DUMPKIND</c>.</para>
    /// </summary>
    public static TheoryData<TestConfig> BuildMatrix(
        string[] targets,
        Flavor flavor = Flavor.AllValid,
        Host host = Host.AllValid,
        Liveness liveness = Liveness.AllValid,
        GcType gcType = GcType.Workstation,
        DumpKind dumpKind = DumpKind.Full,
        bool publicSymbols = false)
    {
        TheoryData<TestConfig> data = new();
        foreach (TestConfig cfg in Permutations(targets, flavor, host, liveness, gcType, dumpKind, publicSymbols))
        {
            data.Add(cfg);
        }

        return data;
    }

    /// <summary>
    /// The raw valid configurations for the requested axes (what <see cref="BuildMatrix"/> wraps into a
    /// theory source). Exposed for theories that need to pair each config with an extra, non-axis column —
    /// e.g. a stop-point name — into their own <c>TheoryData&lt;TestConfig, ...&gt;</c>.
    /// </summary>
    public static IEnumerable<TestConfig> Permutations(
        string[] targets,
        Flavor flavor = Flavor.AllValid,
        Host host = Host.AllValid,
        Liveness liveness = Liveness.AllValid,
        GcType gcType = GcType.Workstation,
        DumpKind dumpKind = DumpKind.Full,
        bool publicSymbols = false)
    {
        HashSet<TestConfig> seen = new();

        foreach (string target in targets)
        {
            foreach (Host h in SingleFlags(host, "SOSHARNESS_ONLY_HOSTS"))
            {
                foreach (Flavor f in SingleFlags(flavor, "SOSHARNESS_ONLY_FLAVORS"))
                {
                    foreach (Liveness l in SingleFlags(liveness, "SOSHARNESS_ONLY_LIVENESS"))
                    {
                        foreach (GcType g in SingleFlags(gcType, "SOSHARNESS_ONLY_GCTYPE"))
                        {
                            foreach (DumpKind d in SingleFlags(dumpKind, "SOSHARNESS_ONLY_DUMPKIND"))
                            {
                                // A live target has no dump, so DumpKind and PublicSymbols don't apply.
                                // Collapse them to canonical values so we emit one live row, not one per
                                // (DumpKind) permutation.
                                DumpKind dk = l == Liveness.Live ? DumpKind.Full : d;
                                bool pub = l == Liveness.Live ? false : publicSymbols;

                                TestConfig cfg = new(target, h, f, l, g, dk, pub);
                                if (IsValid(cfg) && seen.Add(cfg))
                                {
                                    yield return cfg;
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Whether a configuration is valid on the current platform. Centralizes every constraint that the old
    /// nested-loop <c>BuildMatrix</c> scattered across per-axis <c>continue</c>s.
    /// </summary>
    private static bool IsValid(TestConfig c)
    {
        // Host platform constraints: cdb is Windows-only, lldb is non-Windows-only.
        if (c.Host == Host.Cdb && !OperatingSystem.IsWindows())
        {
            return false;
        }

        if (c.Host == Host.Lldb && OperatingSystem.IsWindows())
        {
            return false;
        }

        // Desktop .NET Framework is Windows-only.
        if (c.Flavor == Flavor.Framework && !OperatingSystem.IsWindows())
        {
            return false;
        }

        // dotnet-dump is post-mortem only; it has no live host.
        if (c.IsLive && c.Host == Host.DotnetDump)
        {
            return false;
        }

        // The target must support the requested flavor (e.g. DynamicMethod can't build for Framework).
        if ((TargetCatalog.FlavorsFor(c.Target) & c.Flavor) == 0)
        {
            return false;
        }

        // Server GC is forced via .NET-Core GC env vars (DATAS off + fixed heap count); desktop .NET
        // Framework doesn't honor them, so Server is a Core/SingleFile-only axis.
        if (c.GcType == GcType.Server && c.Flavor == Flavor.Framework)
        {
            return false;
        }

        // Server GC for a LIVE target would require injecting the GC env vars into the dbgeng-launched
        // debuggee process; that isn't wired yet (no consumer), so Server is dump-only for now.
        if (c.GcType == GcType.Server && c.IsLive)
        {
            return false;
        }

        // Framework dumps are full user-mode dumps captured by DbgEng; createdump's reduced dump types
        // don't apply, so Framework supports only Full.
        if (c.DumpKind == DumpKind.Mini && c.Flavor == Flavor.Framework)
        {
            return false;
        }

        // Public OS symbols only make sense for a (cdb) dump host.
        if (c.PublicSymbols && c.IsLive)
        {
            return false;
        }

        return true;
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
    /// allow-list in <paramref name="envVar"/> (enum names, case-insensitive). Lets a run be staged onto a
    /// subset of the matrix during bring-up, e.g. <c>SOSHARNESS_ONLY_FLAVORS=Core</c>,
    /// <c>SOSHARNESS_ONLY_HOSTS=Cdb,DotnetDump</c>, <c>SOSHARNESS_ONLY_GCTYPE=Server</c>.
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

    void IXunitSerializable.Serialize(IXunitSerializationInfo info)
    {
        info.AddValue(nameof(Target), Target, typeof(string));
        info.AddValue(nameof(Host), Host, typeof(Host));
        info.AddValue(nameof(Flavor), Flavor, typeof(Flavor));
        info.AddValue(nameof(Liveness), Liveness, typeof(Liveness));
        info.AddValue(nameof(GcType), GcType, typeof(GcType));
        info.AddValue(nameof(DumpKind), DumpKind, typeof(DumpKind));
        info.AddValue(nameof(PublicSymbols), PublicSymbols, typeof(bool));
    }

    void IXunitSerializable.Deserialize(IXunitSerializationInfo info)
    {
        Target = info.GetValue<string>(nameof(Target))!;
        Host = info.GetValue<Host>(nameof(Host));
        Flavor = info.GetValue<Flavor>(nameof(Flavor));
        Liveness = info.GetValue<Liveness>(nameof(Liveness));
        GcType = info.GetValue<GcType>(nameof(GcType));
        DumpKind = info.GetValue<DumpKind>(nameof(DumpKind));
        PublicSymbols = info.GetValue<bool>(nameof(PublicSymbols));
    }

    /// <summary>
    /// A legible, deterministic id used for the theory display name and de-duplication, e.g.
    /// <c>scenarios/Cdb/Core/Dump/Workstation/Full</c> (the dump kind is omitted for live rows, which have
    /// no dump, and a <c>/pub</c> suffix marks the public-symbol variant).
    /// </summary>
    public override string ToString()
    {
        string dump = IsLive ? string.Empty : "/" + DumpKind;
        string pub = PublicSymbols ? "/pub" : string.Empty;
        return $"{Target}/{Host}/{Flavor}/{Liveness}/{GcType}{dump}{pub}";
    }

    public bool Equals(TestConfig? other) =>
        other is not null
        && Target == other.Target
        && Host == other.Host
        && Flavor == other.Flavor
        && Liveness == other.Liveness
        && GcType == other.GcType
        && DumpKind == other.DumpKind
        && PublicSymbols == other.PublicSymbols;

    public override bool Equals(object? obj) => Equals(obj as TestConfig);

    public override int GetHashCode() =>
        HashCode.Combine(Target, Host, Flavor, Liveness, GcType, DumpKind, PublicSymbols);
}
