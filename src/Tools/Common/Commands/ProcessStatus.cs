// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Internal.Common;
using Microsoft.Internal.Common.Utils;
using Process = System.Diagnostics.Process;

namespace Microsoft.Internal.Common.Commands
{
    public class ProcessStatusCommandHandler
    {
        public static Command ProcessStatusCommand(string description)
        {
            Command statusCommand = new(name: "ps", description);
            statusCommand.SetAction((parseResult, ct) => Task.FromResult(ProcessStatus(parseResult.Configuration.Output, parseResult.Configuration.Error)));
            return statusCommand;
        }

        private static void MakeFixedWidth(string text, int width, StringBuilder sb, bool leftPad = false, bool truncateFront = false)
        {
            int textLength = text.Length;
            sb.Append(' ');
            if (textLength == width)
            {
                sb.Append(text);
            }
            else if (textLength > width)
            {
                if (truncateFront)
                {
                    sb.Append(text.AsSpan(textLength - width, width));
                }
                else
                {
                    sb.Append(text.AsSpan(0, width));
                }
            }
            else
            {
                if (leftPad)
                {
                    sb.Append(' ', width - textLength);
                    sb.Append(text);
                }
                else
                {
                    sb.Append(text);
                    sb.Append(' ', width - text.Length);
                }

            }
            sb.Append(' ');
        }

        private struct ProcessDetails
        {
            public int ProcessId;
            public string ProcessName;
            public string FileName;
            public string CmdLineArgs;
            public string EndpointAddress;
        }

        /// <summary>
        /// Print the current list of available .NET core processes for diagnosis, their statuses and the command line arguments that are passed to them.
        /// </summary>
        public static int ProcessStatus(TextWriter stdOut, TextWriter stdError)
        {
            int GetColumnWidth(IEnumerable<int> fieldWidths)
            {
                int consoleWidth = 0;
                if (Console.IsOutputRedirected)
                {
                    consoleWidth = int.MaxValue;
                }
                else
                {
                    consoleWidth = Console.WindowWidth;
                }
                int extra = (int)Math.Ceiling(consoleWidth * 0.05);
                int largeLength = consoleWidth / 2 - 16 - extra;
                return Math.Min(fieldWidths.Max(), largeLength);
            }

            void FormatTableRows(List<ProcessDetails> rows, StringBuilder tableText)
            {
                if (rows.Count == 0)
                {
                    tableText.Append("No supported .NET processes were found");
                    return;
                }
                IEnumerable<int> processIDs = rows.Select(i => i.ProcessId.ToString().Length);
                IEnumerable<int> processNames = rows.Select(i => i.ProcessName.Length);
                IEnumerable<int> fileNames = rows.Select(i => i.FileName.Length);
                IEnumerable<int> commandLineArgs = rows.Select(i => i.CmdLineArgs.Length);
                int iDLength = GetColumnWidth(processIDs);
                int nameLength = GetColumnWidth(processNames);
                int fileLength = GetColumnWidth(fileNames);
                int cmdLength = GetColumnWidth(commandLineArgs);

                foreach (ProcessDetails info in rows)
                {
                    MakeFixedWidth(info.ProcessId.ToString(), iDLength, tableText, true, true);
                    MakeFixedWidth(info.ProcessName, nameLength, tableText, false, true);
                    MakeFixedWidth(info.FileName, fileLength, tableText, false, true);
                    MakeFixedWidth(info.CmdLineArgs, cmdLength, tableText, false, true);
                    tableText.Append('\n');
                }
            }
            try
            {
                StringBuilder sb = new();
                int currentPid = Process.GetCurrentProcess().Id;
                List<ProcessDetails> printInfo = new();

                // Use GetPublishedEndpoints to discover all endpoints, including
                // multiple endpoints for the same PID in cross-container scenarios.
                foreach (ProcessEndpointInfo endpoint in DiagnosticsClient.GetPublishedEndpoints())
                {
                    if (endpoint.ProcessId == currentPid)
                    {
                        continue;
                    }

                    // First try to get info from the local process table.
                    Process localProcess = GetProcessById(endpoint.ProcessId);
                    if (localProcess != null)
                    {
                        try
                        {
                            string cmdLineArgs = GetArgs(localProcess);
                            cmdLineArgs = cmdLineArgs == localProcess.MainModule?.FileName ? string.Empty : cmdLineArgs;
                            string fileName = localProcess.MainModule?.FileName ?? string.Empty;
                            printInfo.Add(new ProcessDetails
                            {
                                ProcessId = endpoint.ProcessId,
                                ProcessName = localProcess.ProcessName,
                                FileName = fileName,
                                CmdLineArgs = cmdLineArgs,
                                EndpointAddress = endpoint.EndpointAddress
                            });
                        }
                        catch (Exception ex)
                        {
                            if (ex is Win32Exception or InvalidOperationException)
                            {
                                printInfo.Add(new ProcessDetails
                                {
                                    ProcessId = endpoint.ProcessId,
                                    ProcessName = localProcess.ProcessName,
                                    FileName = "[Elevated process - cannot determine path]",
                                    CmdLineArgs = "",
                                    EndpointAddress = endpoint.EndpointAddress
                                });
                            }
                            else
                            {
                                Debug.WriteLine($"[PrintProcessStatus] {ex}");
                            }
                        }
                    }
                    else
                    {
                        // Process not found locally. This is likely a cross-container
                        // endpoint where the PID belongs to another namespace.
                        // Try connecting to the diagnostic endpoint to get process info.
                        string processName = "[Remote/Container]";
                        string cmdLineArgs = "";
                        try
                        {
                            DiagnosticsClient client = new(endpoint.EndpointAddress);
                            ProcessInfo processInfo = client.GetProcessInfo();
                            if (processInfo != null)
                            {
                                processName = !string.IsNullOrEmpty(processInfo.ManagedEntrypointAssemblyName)
                                    ? processInfo.ManagedEntrypointAssemblyName
                                    : processName;
                                cmdLineArgs = processInfo.CommandLine ?? "";
                            }
                        }
                        catch
                        {
                            // Socket may be stale or inaccessible; skip silently.
                            continue;
                        }

                        printInfo.Add(new ProcessDetails
                        {
                            ProcessId = endpoint.ProcessId,
                            ProcessName = processName,
                            FileName = endpoint.EndpointAddress ?? "",
                            CmdLineArgs = cmdLineArgs,
                            EndpointAddress = endpoint.EndpointAddress
                        });
                    }
                }

                // Deduplicate by endpoint address (in case both local and proc discovery found the same one)
                printInfo = printInfo
                    .GroupBy(p => p.EndpointAddress ?? p.ProcessId.ToString())
                    .Select(g => g.First())
                    .OrderBy(p => p.ProcessName)
                    .ThenBy(p => p.ProcessId)
                    .ToList();

                FormatTableRows(printInfo, sb);
                stdOut.WriteLine(sb.ToString());
                return 0;
            }
            catch (Exception ex)
            {
                stdError.WriteLine(ex.ToString());
                return 1;
            }
        }

        private static Process GetProcessById(int processId)
        {
            try
            {
                return Process.GetProcessById(processId);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static string GetArgs(Process process)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    string commandLine = WindowsProcessExtension.GetCommandLine(process);
                    if (!string.IsNullOrWhiteSpace(commandLine))
                    {
                        string[] commandLineSplit = commandLine.Split(' ');
                        if (commandLineSplit.FirstOrDefault() == process.ProcessName)
                        {
                            return string.Join(" ", commandLineSplit.Skip(1));
                        }
                        return commandLine;
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    return "[Elevated process - cannot determine command line arguments]";
                }

            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                try
                {
                    string commandLine = File.ReadAllText($"/proc/{process.Id}/cmdline");
                    if (!string.IsNullOrWhiteSpace(commandLine))
                    {
                        //The command line may be modified and the first part of the command line may not be /path/to/exe. If that is the case, return the command line as is.Else remove the path to module as we are already displaying that.
                        string[] commandLineSplit = commandLine.Split('\0');
                        if (commandLineSplit.FirstOrDefault() == process.MainModule?.FileName)
                        {
                            return string.Join(" ", commandLineSplit.Skip(1));
                        }
                        return commandLine.Replace("\0", " ");
                    }
                    return "";
                }
                catch (IOException)
                {
                    return "[cannot determine command line arguments]";
                }
            }
            return "";
        }
    }
}
