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

    /// <summary>The .NET Core runtime version the target is built and dumped against (a single flag).</summary>
    public CoreVersion CoreVersion { get; private set; }

    /// <summary>Which DAC SOS debugs with (Legacy / CDac). cDAC is only valid on .NET 11+ (see <see cref="IsValid"/>).</summary>
    public Dac Dac { get; private set; }

    /// <summary>The dump kind (Heap / Mini). Always <see cref="DumpKind.Heap"/> for live targets (no dump).</summary>
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
                      GcType gcType = GcType.Workstation, DumpKind dumpKind = DumpKind.Heap, bool publicSymbols = false,
                      CoreVersion coreVersion = CoreVersion.Net10, Dac dac = Dac.Legacy)
    {
        Target = target;
        Host = host;
        Flavor = flavor;
        Liveness = liveness;
        GcType = gcType;
        DumpKind = dumpKind;
        PublicSymbols = publicSymbols;
        CoreVersion = coreVersion;
        Dac = dac;
    }

    /// <summary>True for a live process target; false for a post-mortem dump.</summary>
    public bool IsLive => Liveness == Liveness.Live;

    /// <summary>Return a copy with <see cref="PublicSymbols"/> set (for the OS-symbol Facts).</summary>
    public TestConfig WithPublicSymbols(bool value = true) =>
        new(Target, Host, Flavor, Liveness, GcType, DumpKind, value, CoreVersion, Dac);

    /// <summary>
    /// Generate the cross-product of the requested axes as a single-column theory source, filtered to the
    /// valid configurations for the current platform (see <see cref="IsValid"/>).
    ///
    /// <para>Axis defaults are deliberate: <paramref name="host"/>/<paramref name="flavor"/> default to
    /// <c>AllValid</c> (full coverage), but <paramref name="liveness"/> defaults to
    /// <see cref="Liveness.Dump"/>, <paramref name="gcType"/> to <see cref="GcType.Workstation"/>, and
    /// <paramref name="dumpKind"/> to <see cref="DumpKind.Heap"/>. Live debugging is slow (a debugger
    /// ptrace-attached to a running process, one session per core) and almost every command behaves
    /// identically against a dump, so live coverage is <em>opt-in</em>: a test that uniquely benefits from a
    /// live process (e.g. a stack walk reading live thread contexts, a live GC heap/root scan) passes
    /// <c>liveness: Liveness.AllValid</c> to run dump <em>and</em> live; everything else stays dump-only.
    /// Server GC and Mini dumps are likewise opt-in so the matrix doesn't explode.</para>
    ///
    /// <para>Each axis can be narrowed at run time by a comma-separated env allow-list:
    /// <c>SOSHARNESS_ONLY_HOSTS</c>, <c>_FLAVORS</c>, <c>_LIVENESS</c>, <c>_GCTYPE</c>, <c>_DUMPKIND</c>,
    /// <c>_COREVERSIONS</c> (e.g. <c>Net10,Net11</c>), <c>_DAC</c> (e.g. <c>Legacy</c>).</para></summary>
    public static TheoryData<TestConfig> BuildMatrix(
        string[] targets,
        Flavor flavor = Flavor.AllValid,
        Host host = Host.AllValid,
        Liveness liveness = Liveness.Dump,
        GcType gcType = GcType.Workstation,
        DumpKind dumpKind = DumpKind.Heap,
        bool publicSymbols = false,
        CoreVersion coreVersion = CoreVersion.All,
        Dac dac = Dac.All)
    {
        TheoryData<TestConfig> data = new();
        foreach (TestConfig cfg in Permutations(targets, flavor, host, liveness, gcType, dumpKind, publicSymbols, coreVersion, dac))
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
        Liveness liveness = Liveness.Dump,
        GcType gcType = GcType.Workstation,
        DumpKind dumpKind = DumpKind.Heap,
        bool publicSymbols = false,
        CoreVersion coreVersion = CoreVersion.All,
        Dac dac = Dac.All)
    {
        HashSet<TestConfig> seen = new();

        // Only ever expand versions the harness actually builds/installs; a requested bit outside the
        // available set is silently dropped (the axis disables, it never positively enables — see CoreVersion).
        CoreVersion requestedVersions = coreVersion & CoreVersions.Available;

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
                                foreach (CoreVersion cv in SingleFlags(requestedVersions, "SOSHARNESS_ONLY_COREVERSIONS"))
                                {
                                    foreach (Dac da in SingleFlags(dac, "SOSHARNESS_ONLY_DAC"))
                                    {
                                        // A live target has no dump, so DumpKind and PublicSymbols don't
                                        // apply. Collapse them to canonical values so we emit one live row,
                                        // not one per (DumpKind) permutation.
                                        DumpKind dk = l == Liveness.Live ? DumpKind.Heap : d;
                                        bool pub = l == Liveness.Live ? false : publicSymbols;

                                        TestConfig cfg = new(target, h, f, l, g, dk, pub, cv, da);
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

        // Live bpmd can't bind in a self-contained single-file image under the lldb host: CoreCLR is
        // statically linked into the symbol-stripped app image, so lldb has no symbol on which to set the
        // JIT/prestub notification breakpoint (.NET Core keeps CoreCLR as a distinct libcoreclr.so, so it
        // works there). Prune the (lldb, single-file, live) row for targets navigated via a managed stop
        // point; crash targets, which just run to the fault, keep their live single-file coverage. See
        // issues.md#bpmd-singlefile-live-lldb.
        if (c.IsLive && c.Host == Host.Lldb && c.Flavor == Flavor.SingleFile && TargetCatalog.NavigatesViaBpmd(c.Target))
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

        // Runtime createdump only supports full dumps for single-file apps when it needs the DAC to
        // enumerate reduced-dump regions. Don't generate Mini rows for single-file targets.
        if (c.DumpKind == DumpKind.Mini && c.Flavor == Flavor.SingleFile)
        {
            return false;
        }

        // Public OS symbols only make sense for a (cdb) dump host.
        if (c.PublicSymbols && c.IsLive)
        {
            return false;
        }

        // The cDAC (managed contract DAC) is a .NET Core concept; desktop .NET Framework has no cDAC, so
        // `runtimes --usecdac true` fails on clr.dll ("no matching cDAC is available for this runtime").
        // Prune the CDac axis for the Framework flavor (its CoreVersion label is meaningless anyway).
        if (c.Dac == Dac.CDac && c.Flavor == Flavor.Framework)
        {
            return false;
        }

        // The cDAC (managed contract DAC) only exists on .NET 11+; on earlier runtimes only the legacy
        // native DAC is available, so prune the CDac axis there. The same dump is reused across DAC values
        // (only `runtimes --usecdac` differs at debug time), so this just removes the invalid debug-time
        // variant, never a capture.
        if (c.Dac == Dac.CDac && (uint)c.CoreVersion < (uint)CoreVersion.Net11)
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
        info.AddValue(nameof(CoreVersion), CoreVersion, typeof(CoreVersion));
        info.AddValue(nameof(Dac), Dac, typeof(Dac));
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
        CoreVersion = info.GetValue<CoreVersion>(nameof(CoreVersion));
        Dac = info.GetValue<Dac>(nameof(Dac));
    }

    /// <summary>
    /// A legible, deterministic id used for the theory display name and de-duplication, e.g.
    /// <c>scenarios/Cdb/Core/net10/Dump/Workstation/Heap</c> (the runtime version is always shown; the dump
    /// kind is omitted for live rows; a <c>/cdac</c> suffix marks the cDAC variant and <c>/pub</c> the
    /// public-symbol variant). Legacy DAC is the implicit default and isn't tokenized, so single-DAC ids
    /// stay terse.
    /// </summary>
    public override string ToString()
    {
        string version = "/net" + CoreVersions.Major(CoreVersion);
        string dump = IsLive ? string.Empty : "/" + DumpKind;
        string dac = Dac == Dac.CDac ? "/cdac" : string.Empty;
        string pub = PublicSymbols ? "/pub" : string.Empty;
        return $"{Target}/{Host}/{Flavor}{version}/{Liveness}/{GcType}{dump}{dac}{pub}";
    }

    public bool Equals(TestConfig? other) =>
        other is not null
        && Target == other.Target
        && Host == other.Host
        && Flavor == other.Flavor
        && Liveness == other.Liveness
        && GcType == other.GcType
        && DumpKind == other.DumpKind
        && PublicSymbols == other.PublicSymbols
        && CoreVersion == other.CoreVersion
        && Dac == other.Dac;

    public override bool Equals(object? obj) => Equals(obj as TestConfig);

    public override int GetHashCode() =>
        HashCode.Combine(Target, Host, Flavor, Liveness, GcType, DumpKind, PublicSymbols, HashCode.Combine(CoreVersion, Dac));
}
