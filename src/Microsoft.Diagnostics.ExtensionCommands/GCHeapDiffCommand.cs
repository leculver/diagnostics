// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Diagnostics.DebugServices;
using Microsoft.Diagnostics.ExtensionCommands.Output;
using Microsoft.Diagnostics.Runtime;

namespace Microsoft.Diagnostics.ExtensionCommands
{
    [Command(Name = "gcheapdiff", Aliases = new[] { "GCHeapDiff" }, Help = "Compares the current GC heap to the one contained in a baseline dump.")]
    public sealed class GCHeapDiffCommand : ClrRuntimeCommandBase
    {
        [ServiceImport]
        public IDumpTargetFactory DumpTargetFactory { get; set; }

        [Option(Name = "-all", Help = "Show all type differences, not just the top entries.")]
        public bool ShowAll { get; set; }

        [Option(Name = "-bycount", Help = "Sort by object count delta instead of size delta.")]
        public bool SortByCount { get; set; }

        [Option(Name = "-type", Help = "Filter results to types containing this substring.")]
        public string TypeFilter { get; set; }

        [Argument(Name = "baseline_dump", Help = "Path to the baseline dump file to compare against.")]
        public string BaselineDumpPath { get; set; }

        private const int DefaultTopCount = 20;

        public override void Invoke()
        {
            if (string.IsNullOrWhiteSpace(BaselineDumpPath))
            {
                throw new DiagnosticsException("A baseline dump path is required. Usage: gcheapdiff <path_to_baseline_dump>");
            }

            string fullPath = Path.GetFullPath(BaselineDumpPath);
            if (!File.Exists(fullPath))
            {
                throw new DiagnosticsException($"Baseline dump not found: {fullPath}");
            }

            // Gather stats from the current heap
            Dictionary<string, TypeStats> currentStats = GatherHeapStats(Runtime.Heap);

            // Open the baseline dump and gather its stats
            ITarget baselineTarget = null;
            Dictionary<string, TypeStats> baselineStats;
            try
            {
                baselineTarget = DumpTargetFactory.OpenDump(fullPath);

                ClrRuntime baselineRuntime = GetClrRuntime(baselineTarget);
                if (baselineRuntime == null)
                {
                    throw new DiagnosticsException("No CLR runtime found in the baseline dump.");
                }

                baselineStats = GatherHeapStats(baselineRuntime.Heap);
            }
            finally
            {
                baselineTarget?.Destroy();
            }

            // Compute and display the diff
            List<TypeDiff> diffs = ComputeDiffs(currentStats, baselineStats);

            if (!string.IsNullOrWhiteSpace(TypeFilter))
            {
                diffs = diffs.Where(d => d.TypeName.Contains(TypeFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            // Sort by absolute delta
            if (SortByCount)
            {
                diffs.Sort((a, b) => Math.Abs(b.CountDelta).CompareTo(Math.Abs(a.CountDelta)));
            }
            else
            {
                diffs.Sort((a, b) => Math.Abs(b.SizeDelta).CompareTo(Math.Abs(a.SizeDelta)));
            }

            if (diffs.Count == 0)
            {
                WriteLine("No differences found.");
                return;
            }

            int displayCount = ShowAll ? diffs.Count : Math.Min(DefaultTopCount, diffs.Count);

            if (!ShowAll && diffs.Count > DefaultTopCount)
            {
                WriteLine($"Showing top {DefaultTopCount} GC heap differences by {(SortByCount ? "count" : "size")}");
            }
            else
            {
                WriteLine($"Showing {(ShowAll ? "all" : "top")} GC heap differences by {(SortByCount ? "count" : "size")}");
            }

            WriteLine();

            // Print header
            Table table = new(Console,
                new Column(Align.Left, -40, Formats.Text),
                new Column(Align.Right, 14, Formats.IntegerWithoutCommas),
                new Column(Align.Right, 8, Formats.IntegerWithoutCommas),
                new Column(Align.Right, 14, Formats.IntegerWithoutCommas),
                new Column(Align.Right, 8, Formats.IntegerWithoutCommas),
                new Column(Align.Right, 14, Formats.Text),
                new Column(Align.Right, 8, Formats.Text));

            table.WriteHeader("Type", "Cur Size", "Cur Cnt", "Base Size", "Base Cnt", "Size Delta", "Cnt Delta");

            for (int i = 0; i < displayCount; i++)
            {
                Console.CancellationToken.ThrowIfCancellationRequested();

                TypeDiff diff = diffs[i];
                table.WriteRow(
                    TruncateTypeName(diff.TypeName, 40),
                    diff.CurrentSize,
                    diff.CurrentCount,
                    diff.BaselineSize,
                    diff.BaselineCount,
                    FormatDelta(diff.SizeDelta),
                    FormatDelta(diff.CountDelta));
            }

            WriteLine();

            if (!ShowAll && diffs.Count > DefaultTopCount)
            {
                WriteLine($"To show all differences use 'gcheapdiff -all {BaselineDumpPath}'");
            }

            WriteLine("To show objects of a particular type use 'dumpheap -type <type_name>'");
        }

        private Dictionary<string, TypeStats> GatherHeapStats(ClrHeap heap)
        {
            Dictionary<string, TypeStats> stats = new();

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                Console.CancellationToken.ThrowIfCancellationRequested();

                if (!obj.IsValid || obj.IsFree)
                {
                    continue;
                }

                string typeName = obj.Type?.Name ?? "<unknown>";
                if (!stats.TryGetValue(typeName, out TypeStats existing))
                {
                    existing = new TypeStats();
                    stats[typeName] = existing;
                }

                existing.Count++;
                existing.Size += obj.Size;
            }

            return stats;
        }

        private static ClrRuntime GetClrRuntime(ITarget target)
        {
            IRuntimeService runtimeService = target.Services.GetService<IRuntimeService>();
            if (runtimeService == null)
            {
                return null;
            }

            foreach (IRuntime runtime in runtimeService.EnumerateRuntimes())
            {
                ClrRuntime clrRuntime = runtime.Services.GetService<ClrRuntime>();
                if (clrRuntime != null)
                {
                    return clrRuntime;
                }
            }

            return null;
        }

        private static List<TypeDiff> ComputeDiffs(Dictionary<string, TypeStats> current, Dictionary<string, TypeStats> baseline)
        {
            HashSet<string> allTypes = new(current.Keys);
            allTypes.UnionWith(baseline.Keys);

            List<TypeDiff> diffs = new();
            foreach (string typeName in allTypes)
            {
                current.TryGetValue(typeName, out TypeStats curStats);
                baseline.TryGetValue(typeName, out TypeStats baseStats);

                long curCount = curStats?.Count ?? 0;
                long curSize = (long)(curStats?.Size ?? 0);
                long baseCount = baseStats?.Count ?? 0;
                long baseSize = (long)(baseStats?.Size ?? 0);

                long countDelta = curCount - baseCount;
                long sizeDelta = curSize - baseSize;

                if (countDelta != 0 || sizeDelta != 0)
                {
                    diffs.Add(new TypeDiff
                    {
                        TypeName = typeName,
                        CurrentCount = curCount,
                        CurrentSize = curSize,
                        BaselineCount = baseCount,
                        BaselineSize = baseSize,
                        CountDelta = countDelta,
                        SizeDelta = sizeDelta
                    });
                }
            }

            return diffs;
        }

        private static string FormatDelta(long delta)
        {
            if (delta > 0)
            {
                return $"+{delta}";
            }

            return delta.ToString();
        }

        private static string TruncateTypeName(string typeName, int maxLength)
        {
            if (typeName.Length <= maxLength)
            {
                return typeName;
            }

            return "..." + typeName.Substring(typeName.Length - maxLength + 3);
        }

        [HelpInvoke]
        public static string GetDetailedHelp() =>
@"gcheapdiff compares the managed GC heap of the current dump to a baseline dump,
showing which types have grown or shrunk in count and total size.

This is useful for investigating memory leaks: take two dumps at different times,
then compare them to see which types are accumulating.

Usage:
    gcheapdiff <path_to_baseline_dump>
    gcheapdiff -all <path_to_baseline_dump>
    gcheapdiff -bycount <path_to_baseline_dump>
    gcheapdiff -type <substring> <path_to_baseline_dump>

Examples:
    gcheapdiff ./baseline.dmp
        Show the top 20 type differences by size between current and baseline dumps.

    gcheapdiff -all ./baseline.dmp
        Show all type differences.

    gcheapdiff -type System.String ./baseline.dmp
        Show differences only for types containing 'System.String'.

    gcheapdiff -bycount ./baseline.dmp
        Sort differences by object count delta instead of size delta.

After identifying a growing type, use 'dumpheap -type <type_name>' to list
instances, then 'gcroot <address>' to find what is keeping them alive.
";

        private sealed class TypeStats
        {
            public long Count;
            public ulong Size;
        }

        private sealed class TypeDiff
        {
            public string TypeName;
            public long CurrentCount;
            public long CurrentSize;
            public long BaselineCount;
            public long BaselineSize;
            public long CountDelta;
            public long SizeDelta;
        }
    }
}
