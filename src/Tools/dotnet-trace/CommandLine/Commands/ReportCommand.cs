// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Diagnostics.Tracing.StackSources;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tools.Trace.CommandLine;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Stacks;
using Microsoft.Internal.Common;

namespace Microsoft.Diagnostics.Tools.Trace
{
    internal static class ReportCommandHandler
    {
        private static List<string> unwantedMethodNames = new() { "ROOT", "Process" };

        //Create an extension function to help
        public static List<CallTreeNodeBase> ByIDSortedInclusiveMetric(this CallTree callTree)
        {
            List<CallTreeNodeBase> ret = new(callTree.ByID);
            ret.Sort((x, y) => Math.Abs(y.InclusiveMetric).CompareTo(Math.Abs(x.InclusiveMetric)));
            return ret;
        }

        private static Task<int> Report()
        {
            Console.Error.WriteLine("Error: subcommand was not provided. Available subcommands:");
            Console.Error.WriteLine("    topN: Finds the top N methods on the callstack the longest.");
            Console.Error.WriteLine("    events: Lists individual events from the trace.");
            return Task.FromResult(-1);
        }

        private static int TopNReport(string traceFile, int number, bool inclusive, bool verbose)
        {
            try
            {
                string tempEtlxFilename = TraceLog.CreateFromEventPipeDataFile(traceFile);
                int count = 0;
                int index = 0;
                List<CallTreeNodeBase> nodesToReport = new();
                using (SymbolReader symbolReader = new(System.IO.TextWriter.Null) { SymbolPath = SymbolPath.MicrosoftSymbolServerPath })
                using (TraceLog eventLog = new(tempEtlxFilename))
                {
                    MutableTraceEventStackSource stackSource = new(eventLog)
                    {
                        OnlyManagedCodeStacks = true
                    };

                    SampleProfilerThreadTimeComputer computer = new(eventLog, symbolReader);

                    computer.GenerateThreadTimeStacks(stackSource);

                    FilterParams filterParams = new()
                    {
                        FoldRegExs = "CPU_TIME;UNMANAGED_CODE_TIME;{Thread (}",
                    };
                    FilterStackSource filterStack = new(filterParams, stackSource, ScalingPolicyKind.ScaleToData);
                    CallTree callTree = new(ScalingPolicyKind.ScaleToData);
                    callTree.StackSource = filterStack;

                    List<CallTreeNodeBase> callTreeNodes = null;

                    if (!inclusive)
                    {
                        callTreeNodes = callTree.ByIDSortedExclusiveMetric();
                    }
                    else
                    {
                        callTreeNodes = callTree.ByIDSortedInclusiveMetric();
                    }

                    int totalElements = callTreeNodes.Count;
                    while (count < number && index < totalElements)
                    {
                        CallTreeNodeBase node = callTreeNodes[index];
                        index++;
                        if (!unwantedMethodNames.Any(node.Name.Contains))
                        {
                            nodesToReport.Add(node);
                            count++;
                        }
                    }

                    PrintReportHelper.TopNWriteToStdOut(nodesToReport, inclusive, verbose);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ERROR] {ex}");
                return 1;
            }
        }


        private static int EventsReport(string traceFile, string providerFilter, string eventFilter, string format)
        {
            try
            {
                if (!File.Exists(traceFile))
                {
                    Console.Error.WriteLine($"[ERROR] File not found: {traceFile}");
                    return 1;
                }

                using EventPipeEventSource source = new(traceFile);
                bool asJson = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);

                source.Dynamic.All += (TraceEvent data) =>
                {
                    if (providerFilter != null &&
                        !data.ProviderName.Contains(providerFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (eventFilter != null &&
                        !data.EventName.Contains(eventFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (asJson)
                    {
                        WriteEventAsJson(data);
                    }
                    else
                    {
                        WriteEventAsText(data);
                    }
                };

                source.Process();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ERROR] {ex}");
                return 1;
            }
        }

        private static void WriteEventAsText(TraceEvent data)
        {
            Console.Write($"{data.TimeStamp:o} {data.ProviderName}/{data.EventName}");

            string[] payloadNames = data.PayloadNames;
            if (payloadNames != null && payloadNames.Length > 0)
            {
                Console.Write(" ");
                for (int i = 0; i < payloadNames.Length; i++)
                {
                    if (i > 0)
                    {
                        Console.Write(", ");
                    }

                    object value = data.PayloadValue(i);
                    Console.Write($"{payloadNames[i]}={value}");
                }
            }

            Console.WriteLine();
        }

        private static void WriteEventAsJson(TraceEvent data)
        {
            Dictionary<string, object> eventObj = new()
            {
                ["timestamp"] = data.TimeStamp.ToString("o"),
                ["provider"] = data.ProviderName,
                ["event"] = data.EventName,
                ["processId"] = data.ProcessID,
                ["threadId"] = data.ThreadID,
            };

            string[] payloadNames = data.PayloadNames;
            if (payloadNames != null && payloadNames.Length > 0)
            {
                Dictionary<string, object> payload = new();
                for (int i = 0; i < payloadNames.Length; i++)
                {
                    object value = data.PayloadValue(i);
                    payload[payloadNames[i]] = value?.ToString();
                }

                eventObj["payload"] = payload;
            }

            Console.WriteLine(JsonSerializer.Serialize(eventObj));
        }

        public static Command ReportCommand()
        {
            Command topNCommand = new(
                name: "topN",
                description: "Finds the top N methods that have been on the callstack the longest.")
            {
                TopNOption,
                InclusiveOption,
                VerboseOption,
            };

            topNCommand.SetAction((parseResult, ct) => Task.FromResult(TopNReport(
                traceFile: parseResult.GetValue(FileNameArgument),
                number: parseResult.GetValue(TopNOption),
                inclusive: parseResult.GetValue(InclusiveOption),
                verbose: parseResult.GetValue(VerboseOption)
            )));

            Command eventsCommand = new(
                name: "events",
                description: "Lists individual events from the trace with timestamps and payload data.")
            {
                ProviderFilterOption,
                EventFilterOption,
                FormatOption,
            };

            eventsCommand.SetAction((parseResult, ct) => Task.FromResult(EventsReport(
                traceFile: parseResult.GetValue(FileNameArgument),
                providerFilter: parseResult.GetValue(ProviderFilterOption),
                eventFilter: parseResult.GetValue(EventFilterOption),
                format: parseResult.GetValue(FormatOption)
            )));

            Command reportCommand = new(
                name: "report",
                description: "Generates a report into stdout from a previously generated trace.")
                {
                    FileNameArgument,
                    topNCommand,
                    eventsCommand
                };
            reportCommand.SetAction((parseResult, ct) => Report());

            return reportCommand;
        }

        private static readonly Argument<string> FileNameArgument =
            new("trace_filename")
            {
                Description = "The file path for the trace being analyzed.",
                Arity = new ArgumentArity(1, 1)
            };

        private static readonly Option<int> TopNOption =
            new("--number", "-n")
            {
                Description = "Gives the top N methods on the callstack.",
                DefaultValueFactory = _ => 5
            };

        private static readonly Option<bool> InclusiveOption =
            new("--inclusive")
            {
                Description = "Output the top N methods based on inclusive time. If not specified, exclusive time is used by default."
            };

        private static readonly Option<bool> VerboseOption =
            new("--verbose", "-v")
            {
                Description = "Output the parameters of each method in full. If not specified, parameters will be truncated."
            };

        private static readonly Option<string> ProviderFilterOption =
            new("--provider")
            {
                Description = "Filter events to a specific provider name (substring match, case-insensitive)."
            };

        private static readonly Option<string> EventFilterOption =
            new("--event")
            {
                Description = "Filter events to a specific event name (substring match, case-insensitive)."
            };

        private static readonly Option<string> FormatOption =
            new("--format", "-f")
            {
                Description = "Output format: text (default) or json (one JSON object per line).",
                DefaultValueFactory = _ => "text"
            };
    }
}
