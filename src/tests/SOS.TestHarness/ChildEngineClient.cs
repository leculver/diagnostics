// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace SOS.TestHarness;

/// <summary>
/// The test-host side of the subprocess dbgeng backend: spawns an <c>EngineHost</c> child that
/// hosts dbgeng in-process and drives it over <see cref="EngineProtocol"/>. From the test host's
/// perspective this is just another child-process REPL (like <see cref="DotNetDumpHost"/>), so the
/// test host never loads dbgeng/SOS/DAC and can't be crashed by them. Because each target is its
/// own child process, many can be alive at once — lifting the single-instance limit that
/// in-process dbgeng imposed.
///
/// The child blocks on stdin between commands (no busy-wait), so idle clients are cheap.
/// </summary>
public sealed class ChildEngineClient : IDebuggerHost
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly BlockingCollection<string> _lines = new();
    private readonly Thread _reader;

    public string Name { get; }

    private ChildEngineClient(string name, string mode, IReadOnlyList<string> modeArgs)
    {
        Name = name;

        ProcessStartInfo psi = new()
        {
            FileName = RepoLayout.DotNetExe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(SnapshotStore.EngineHostDll);
        psi.ArgumentList.Add(mode);
        foreach (string a in modeArgs)
        {
            psi.ArgumentList.Add(a);
        }

        // Hermetic, local-only symbols (the dev's _NT_SYMBOL_PATH may point at the Azure-authed symweb,
        // which crashes SOS host init and makes tests network-dependent).
        Directory.CreateDirectory(RepoLayout.SymbolCache);
        psi.Environment["_NT_SYMBOL_PATH"] = RepoLayout.SymbolCache;

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start EngineHost");
        _stdin = _process.StandardInput;

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"enginehost-reader-{name}" };
        _reader.Start();

        WaitForReady(TimeSpan.FromSeconds(120));
    }

    /// <summary>A child engine over a crash/snapshot dump.</summary>
    public static ChildEngineClient ForDump(string hostName, string dumpPath) =>
        new(hostName, "dump", new[] { dumpPath });

    /// <summary>A live child engine that launches the target (parked at the loader break, SOS loaded).</summary>
    public static ChildEngineClient ForLive(string hostName, string exePath) =>
        new(hostName, "live", new[] { exePath });

    public void LoadSos()
    {
        // The child already loads SOS when it opens the target; nothing to do.
    }

    public SosOutput Execute(string command) => new(Name, command, Send(command));

    public SosOutput Sos(string command) => new(Name, command, Send("!" + command));

    /// <summary>Live only: set a managed breakpoint and run to it (handled inside the child).</summary>
    public SosOutput RunToBpmd(string module, string method) =>
        new(Name, $"bpmd {module} {method}", Send(EngineProtocol.RunToBpmdPrefix + module + " " + method));

    /// <summary>Live only: run the process to its second-chance crash (handled inside the child).</summary>
    public SosOutput RunToCrash() =>
        new(Name, "run-to-crash", Send(EngineProtocol.RunToCrash));

    /// <summary>Live only: resume to the next breakpoint (handled inside the child).</summary>
    public SosOutput RunToBreakpoint() =>
        new(Name, "run-to-breakpoint", Send(EngineProtocol.RunToBreak));

    private string Send(string command)
    {
        _stdin.WriteLine(command);
        _stdin.Flush();
        return DrainToEnd(TimeSpan.FromSeconds(120), command);
    }

    private void WaitForReady(TimeSpan timeout)
    {
        while (true)
        {
            if (!_lines.TryTake(out string? line, timeout))
            {
                throw new TimeoutException("EngineHost did not become ready in time.");
            }

            if (line == EngineProtocol.Ready)
            {
                return;
            }
        }
    }

    private string DrainToEnd(TimeSpan timeout, string command)
    {
        StringBuilder sb = new();
        while (true)
        {
            if (!_lines.TryTake(out string? line, timeout))
            {
                throw new TimeoutException($"EngineHost did not return output for '{command}' within {timeout}.");
            }

            if (line == EngineProtocol.End)
            {
                break;
            }

            if (line == EngineProtocol.Error)
            {
                // The child threw while processing this command (e.g. RunToBreakpoint hit a crash or
                // the process exited). Surface it as an exception rather than returning silently with
                // a dead session that later commands would fail against.
                throw new InvalidOperationException(
                    $"EngineHost command '{command}' failed: {sb.ToString().TrimEnd()}");
            }

            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    private void ReadLoop()
    {
        string? line;
        while ((line = _process.StandardOutput.ReadLine()) is not null)
        {
            _lines.Add(line);
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _stdin.Close(); // EOF -> child's ReadLine returns null -> clean exit
                if (!_process.WaitForExit(5000))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch
        {
            // best effort
        }
        finally
        {
            _process.Dispose();
        }
    }
}
