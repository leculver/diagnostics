// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace SOS.TestHarness;

/// <summary>How a named stop point is realized when producing a dump.</summary>
public enum StopKind
{
    /// <summary>Mid-run self-snapshot (the debuggee dumps itself and continues).</summary>
    Snapshot,

    /// <summary>The final unhandled-exception crash dump produced by the runtime.</summary>
    Crash,
}

/// <summary>
/// A named location in a target. The same definition drives both worlds: a dump for the
/// snapshot/shared path, and a <c>bpmd</c> breakpoint on <see cref="Method"/> for the live path.
/// </summary>
/// <param name="Name">Stable name used to key the dump and to ask a live target to stop here.</param>
/// <param name="Kind">How the dump for this stop is produced.</param>
/// <param name="Method">Fully-qualified marker method for live <c>bpmd</c> (null for crash stops).</param>
public sealed record StopPoint(string Name, StopKind Kind, string? Method);

/// <summary>A standalone test target (its own program) and its stop points.</summary>
/// <param name="Name">Target name used by tests, e.g. "gcpromotion".</param>
/// <param name="Project">
/// The target's project/assembly name under <c>testtargets/</c>, e.g. "GcPromotion". This is the
/// folder, the csproj, and the produced <c>&lt;Project&gt;.exe</c> / <c>&lt;Project&gt;.dll</c>.
/// </param>
/// <param name="StopPoints">Ordered named stop points.</param>
public sealed record TargetDefinition(string Name, string Project, IReadOnlyList<StopPoint> StopPoints)
{
    /// <summary>Managed module name for <c>bpmd</c> on .NET Core (e.g. "GcPromotion.dll").</summary>
    public string Module => Project + ".dll";

    /// <summary>
    /// Managed module name for <c>bpmd</c> in a given flavor. Desktop .NET Framework's managed
    /// module is the EXE itself (e.g. "GcPromotion.exe"); .NET Core's is the DLL.
    /// </summary>
    public string ModuleFor(Flavor flavor) => flavor == Flavor.Framework ? Project + ".exe" : Project + ".dll";

    public StopPoint Stop(string name) =>
        StopPoints.FirstOrDefault(s => s.Name == name)
        ?? throw new ArgumentException($"Target '{Name}' has no stop point '{name}'. Known: {string.Join(", ", StopPoints.Select(s => s.Name))}");

    public string DefaultStopName => StopPoints[0].Name;
}

/// <summary>The targets this PoC knows about.</summary>
public static class TargetCatalog
{
    public const string GcPromotion = "gcpromotion";
    public const string NestedException = "nestedexception";

    // Targets ported from the legacy SOS.UnitTests Debuggees (the debuggees exercised by the
    // !clrstack scripts). Crash targets reproduce an unhandled exception / access violation; snapshot
    // targets replace the old Debugger.Break() calls with NoInlining Stop_* marker methods.
    public const string SimpleThrow = "simplethrow";
    public const string DivZero = "divzero";
    public const string AsyncMain = "asyncmain";
    public const string DynamicMethod = "dynamicmethod";
    public const string LineNums = "linenums";
    public const string Reflection = "reflection";
    public const string InterpreterStackTest = "interpreterstacktest";
    public const string MiniDumpLocalVarLookup = "minidumplocalvarlookup";
    public const string FindRootsOlderGeneration = "findrootsoldergeneration";
    public const string VarargPInvokeInteropMD = "varargpinvokeinteropmd";

    // Original (not ported) target that pins/refs objects on the stack at a marker so !clrstack -gc
    // reliably prints normal, (pinned) (interior), and (interior) roots for the parser to exercise.
    public const string GcRoots = "gcroots";

    // Original (not ported) target with named primitive + uniquely-typed reference params and locals
    // held live at a marker, for !clrstack -p / -l / -a value checks (incl. the dumpheap oracle).
    public const string ArgsLocals = "argslocals";

    // Original (not ported) target with several worker threads parked at a known method, for the
    // multi-thread enumeration of !clrstack -all.
    public const string ManagedThreads = "managedthreads";

    // Original (not ported) target that holds a rooted live object, an unreachable dead object, and a
    // known-large rooted array at a marker stop, for !dumpheap (-type/-mt/-short, -live/-dead, -min/-max).
    public const string DumpHeapScenario = "dumpheapscenario";

    private static readonly Dictionary<string, TargetDefinition> s_targets = new[]
    {
        new TargetDefinition(
            GcPromotion,
            Project: "GcPromotion",
            StopPoints: new[]
            {
                new StopPoint("gen0", StopKind.Snapshot, "GcPromotion.AtGen0"),
                new StopPoint("gen1", StopKind.Snapshot, "GcPromotion.AtGen1"),
                new StopPoint("gen2", StopKind.Snapshot, "GcPromotion.AtGen2"),
            }),

        new TargetDefinition(
            NestedException,
            Project: "NestedExceptions",
            StopPoints: new[]
            {
                new StopPoint("crash", StopKind.Crash, null),
            }),

        // --- Ported crash targets (unhandled exception / AV -> crash dump). ---

        new TargetDefinition(
            SimpleThrow,
            Project: "SimpleThrow",
            StopPoints: new[]
            {
                new StopPoint("crash", StopKind.Crash, null),
            }),

        new TargetDefinition(
            DivZero,
            Project: "DivZero",
            StopPoints: new[]
            {
                new StopPoint("crash", StopKind.Crash, null),
            }),

        new TargetDefinition(
            AsyncMain,
            Project: "AsyncMain",
            StopPoints: new[]
            {
                new StopPoint("crash", StopKind.Crash, null),
            }),

        new TargetDefinition(
            DynamicMethod,
            Project: "DynamicMethod",
            StopPoints: new[]
            {
                new StopPoint("crash", StopKind.Crash, null),
            }),

        new TargetDefinition(
            LineNums,
            Project: "LineNums",
            StopPoints: new[]
            {
                new StopPoint("crash", StopKind.Crash, null),
            }),

        new TargetDefinition(
            Reflection,
            Project: "ReflectionTest",
            StopPoints: new[]
            {
                new StopPoint("crash", StopKind.Crash, null),
            }),

        new TargetDefinition(
            InterpreterStackTest,
            Project: "InterpreterStackTest",
            StopPoints: new[]
            {
                new StopPoint("crash", StopKind.Crash, null),
            }),

        // --- Ported snapshot targets (former Debugger.Break() points). ---

        new TargetDefinition(
            MiniDumpLocalVarLookup,
            Project: "MiniDumpLocalVarLookup",
            StopPoints: new[]
            {
                new StopPoint("locals", StopKind.Snapshot, "MiniDumpLocalVarLookup.Program.Stop_Locals"),
            }),

        new TargetDefinition(
            FindRootsOlderGeneration,
            Project: "FindRootsOlderGeneration",
            StopPoints: new[]
            {
                new StopPoint("allocated", StopKind.Snapshot, "FindRootsOlderGeneration.Program.Stop_Allocated"),
                new StopPoint("beforegc", StopKind.Snapshot, "FindRootsOlderGeneration.Program.Stop_BeforeGc"),
                new StopPoint("aftergc", StopKind.Snapshot, "FindRootsOlderGeneration.Program.Stop_AfterGc"),
            }),

        new TargetDefinition(
            VarargPInvokeInteropMD,
            Project: "VarargPInvokeInteropMD",
            StopPoints: new[]
            {
                new StopPoint("beforevararg", StopKind.Snapshot, "VarargPInvokeInteropMD.Program.Stop_BeforeVararg"),
            }),

        new TargetDefinition(
            GcRoots,
            Project: "GcRoots",
            StopPoints: new[]
            {
                new StopPoint("roots", StopKind.Snapshot, "GcRoots.AtRoots"),
            }),

        new TargetDefinition(
            ArgsLocals,
            Project: "ArgsLocals",
            StopPoints: new[]
            {
                new StopPoint("argslocals", StopKind.Snapshot, "ArgsLocals.AtArgsLocals"),
            }),

        new TargetDefinition(
            ManagedThreads,
            Project: "ManagedThreads",
            StopPoints: new[]
            {
                new StopPoint("allthreads", StopKind.Snapshot, "ManagedThreads.AtAllThreads"),
            }),

        new TargetDefinition(
            DumpHeapScenario,
            Project: "DumpHeapScenario",
            StopPoints: new[]
            {
                new StopPoint("heap", StopKind.Snapshot, "DumpHeapScenario.AtHeap"),
            }),
    }.ToDictionary(t => t.Name);

    public static TargetDefinition Get(string name) =>
        s_targets.TryGetValue(name, out TargetDefinition? t)
            ? t
            : throw new ArgumentException($"Unknown target '{name}'. Known: {string.Join(", ", s_targets.Keys)}");
}
