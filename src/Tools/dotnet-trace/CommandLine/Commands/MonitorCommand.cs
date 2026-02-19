// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tools;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Internal.Common.Utils;

namespace Microsoft.Diagnostics.Tools.Trace
{
    internal static class MonitorCommandHandler
    {
        /// <summary>
        /// Connects to a running process via EventPipe and prints events to the console in real-time.
        /// </summary>
        internal static async Task<int> Monitor(
            CancellationToken ct,
            int processId,
            string[] providers,
            string[] profile,
            string clrevents,
            string clreventlevel,
            string name,
            string diagnosticPort,
            TimeSpan duration,
            string dsrouter,
            uint buffersize,
            bool counters)
        {
            int ret = (int)ReturnCode.Ok;

            try
            {
                CommandUtils.ResolveProcessForAttach(processId, name, diagnosticPort, dsrouter, out int resolvedProcessId);
                processId = resolvedProcessId;

                if (profile.Length == 0 && providers.Length == 0 && clrevents.Length == 0)
                {
                    Console.WriteLine("No profile or providers specified, defaulting to trace profile 'dotnet-common'.");
                    profile = new[] { "dotnet-common" };
                }

                List<EventPipeProvider> providerCollection = ProviderUtils.ComputeProviderConfig(
                    providers, clrevents, clreventlevel, profile, shouldPrintProviders: true, verbExclusivity: "monitor");
                if (providerCollection.Count <= 0)
                {
                    Console.Error.WriteLine("No providers were specified to start monitoring.");
                    return (int)ReturnCode.ArgumentError;
                }

                Console.WriteLine("Press <Ctrl+C> to stop monitoring.");
                Console.WriteLine();

                DiagnosticsClientBuilder builder = new("dotnet-trace", 10);
                using DiagnosticsClientHolder holder = await builder.Build(ct, processId, diagnosticPort, showChildIO: false, printLaunchCommand: false).ConfigureAwait(false);
                if (holder == null)
                {
                    return (int)ReturnCode.Ok;
                }

                DiagnosticsClient diagnosticsClient = holder.Client;
                EventPipeSessionConfiguration config = new(providerCollection, (int)buffersize, rundownKeyword: 0, requestStackwalk: false);
                using EventPipeSession session = await diagnosticsClient.StartEventPipeSessionAsync(config, ct).ConfigureAwait(false);

                ManualResetEvent shouldExit = new(false);
                ct.Register(() => shouldExit.Set());

                if (duration != default)
                {
                    System.Timers.Timer durationTimer = new(duration.TotalMilliseconds);
                    durationTimer.Elapsed += (s, e) => shouldExit.Set();
                    durationTimer.AutoReset = false;
                    durationTimer.Start();
                }

                // Process events on a background thread
                Task processTask = Task.Run(() =>
                {
                    EventPipeEventSource source = new(session.EventStream);

                    source.Dynamic.All += (TraceEvent data) =>
                    {
                        PrintEvent(data, counters);
                    };

                    source.Clr.All += (TraceEvent data) =>
                    {
                        PrintEvent(data, counters);
                    };

                    source.Process();
                    shouldExit.Set();
                }, CancellationToken.None);

                shouldExit.WaitOne();
                session.Stop();
                processTask.Wait(TimeSpan.FromSeconds(10));

                Console.WriteLine();
                Console.WriteLine("Monitor completed.");
            }
            catch (DiagnosticToolException dte)
            {
                Console.Error.WriteLine($"[ERROR] {dte.Message}");
                ret = (int)dte.ReturnCode;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine();
                Console.WriteLine("Monitoring canceled.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ERROR] {ex}");
                ret = (int)ReturnCode.TracingError;
            }

            return ret;
        }

        private static void PrintEvent(TraceEvent data, bool counters)
        {
            // Skip manifest and rundown events by default
            if (data.ID == (TraceEventID)0xFFFE || // EventPipeMetadata
                data.Opcode == TraceEventOpcode.DataCollectionStart ||
                data.Opcode == TraceEventOpcode.DataCollectionStop)
            {
                return;
            }

            // For counter events, only print if --counters is specified
            if (string.Equals(data.ProviderName, "System.Diagnostics.Metrics", StringComparison.Ordinal) ||
                (data.ProviderName?.StartsWith("System.Runtime", StringComparison.Ordinal) == true
                 && string.Equals(data.EventName, "EventCounters", StringComparison.Ordinal)))
            {
                if (!counters)
                {
                    return;
                }
            }

            StringBuilder sb = new();
            sb.Append('[');
            sb.Append(data.TimeStamp.ToString("HH:mm:ss.ffffff"));
            sb.Append("] ");
            sb.Append(data.ProviderName);
            sb.Append('/');
            sb.Append(data.EventName);

            string[] payloadNames = data.PayloadNames;
            if (payloadNames != null && payloadNames.Length > 0)
            {
                sb.Append(" { ");
                for (int i = 0; i < payloadNames.Length; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    sb.Append(payloadNames[i]);
                    sb.Append('=');
                    try
                    {
                        object value = data.PayloadValue(i);
                        sb.Append(value?.ToString() ?? "null");
                    }
                    catch
                    {
                        sb.Append("<?>");
                    }
                }
                sb.Append(" }");
            }

            Console.WriteLine(sb.ToString());
        }

        public static Command MonitorCommand()
        {
            Command monitorCommand = new("monitor")
            {
                CommonOptions.ProcessIdOption,
                CommonOptions.ProvidersOption,
                CommonOptions.ProfileOption,
                CommonOptions.CLREventsOption,
                CommonOptions.CLREventLevelOption,
                CommonOptions.NameOption,
                CommonOptions.DurationOption,
                DiagnosticPortOption,
                DSRouterOption,
                BufferSizeOption,
                CountersOption
            };

            monitorCommand.Description = "Connects to a running process via EventPipe and prints trace events to the console in real-time. Unlike 'collect', no trace file is written.";

            monitorCommand.SetAction((parseResult, ct) =>
            {
                string providersValue = parseResult.GetValue(CommonOptions.ProvidersOption) ?? string.Empty;
                string profileValue = parseResult.GetValue(CommonOptions.ProfileOption) ?? string.Empty;

                return MonitorCommandHandler.Monitor(ct,
                    processId: parseResult.GetValue(CommonOptions.ProcessIdOption),
                    providers: providersValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    profile: profileValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    clrevents: parseResult.GetValue(CommonOptions.CLREventsOption) ?? string.Empty,
                    clreventlevel: parseResult.GetValue(CommonOptions.CLREventLevelOption) ?? string.Empty,
                    name: parseResult.GetValue(CommonOptions.NameOption),
                    diagnosticPort: parseResult.GetValue(DiagnosticPortOption) ?? string.Empty,
                    duration: parseResult.GetValue(CommonOptions.DurationOption),
                    dsrouter: parseResult.GetValue(DSRouterOption),
                    buffersize: parseResult.GetValue(BufferSizeOption),
                    counters: parseResult.GetValue(CountersOption));
            });

            return monitorCommand;
        }

        private const uint DefaultCircularBufferSizeInMB = 256;

        private static readonly Option<uint> BufferSizeOption =
            new("--buffersize")
            {
                Description = $"Sets the size of the in-memory circular buffer in megabytes. Default {DefaultCircularBufferSizeInMB} MB.",
                DefaultValueFactory = _ => DefaultCircularBufferSizeInMB,
            };

        private static readonly Option<string> DiagnosticPortOption =
            new("--diagnostic-port", "--dport")
            {
                Description = @"The path to a diagnostic port to be used."
            };

        private static readonly Option<string> DSRouterOption =
            new("--dsrouter")
            {
                Description = @"The dsrouter command to start. Value should be one of ios, ios-sim, android, android-emu."
            };

        private static readonly Option<bool> CountersOption =
            new("--counters")
            {
                Description = "Include counter events in the output. Counter events are excluded by default to reduce noise."
            };
    }
}
