// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace SOS.TestHarness;

/// <summary>
/// The "dotnet-dump" host: drives <c>dotnet-dump analyze &lt;dump&gt;</c> as a child process and
/// talks to its REPL over stdin/stdout. Per-command output is delimited by the
/// <c>&lt;END_COMMAND_OUTPUT&gt;</c> marker that the dotnet-dump REPL emits natively after the
/// banner and after every command — the same marker the legacy SOS harness keys on.
///
/// SOS commands are bare here (no <c>!</c> prefix), so <see cref="Sos"/> passes the command
/// through unchanged while the dbgeng host adds the <c>!</c>.
/// </summary>
public sealed class DotNetDumpHost : IDebuggerHost
{
    private const string EndMarker = "<END_COMMAND_OUTPUT>";
    private const string ErrorMarker = "<END_COMMAND_ERROR>";

    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly BlockingCollection<string> _lines = new();
    private readonly Thread _reader;

    public string Name => "dotnet-dump";

    public DotNetDumpHost(string dumpPath)
    {
        ProcessStartInfo psi = new()
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Drive the repo-built dotnet-dump as `dotnet <dll> analyze <dump>` so the harness always
        // validates the freshly-built tool, not a machine-wide install.
        psi.FileName = RepoLayout.DotNetExe;
        psi.ArgumentList.Add(ToolPaths.DotNetDumpDll);
        psi.ArgumentList.Add("analyze");
        psi.ArgumentList.Add(dumpPath);

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet-dump");
        _stdin = _process.StandardInput;

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "dotnet-dump-reader" };
        _reader.Start();

        // Drain the startup banner up to the first marker so the host is ready for commands.
        DrainToMarker(TimeSpan.FromSeconds(120));
    }

    public void LoadSos()
    {
        // SOS is built into dotnet-dump's analyze host; nothing to load.
    }

    public SosOutput Execute(string command) => new(Name, command, Run(command));

    public SosOutput Sos(string command) => new(Name, command, Run(command));

    private string Run(string command)
    {
        _stdin.WriteLine(command);
        _stdin.Flush();
        return DrainToMarker(TimeSpan.FromSeconds(120), command);
    }

    /// <summary>
    /// Collect output lines until the end marker. Strips the echoed prompt line
    /// (<c>"&gt; command"</c>) that dotnet-dump prints when stdin is redirected.
    /// </summary>
    private string DrainToMarker(TimeSpan timeout, string? command = null)
    {
        StringBuilder sb = new();
        while (true)
        {
            if (!_lines.TryTake(out string? line, timeout))
            {
                throw new TimeoutException($"dotnet-dump did not return output for '{command ?? "<startup>"}' within {timeout}.");
            }

            string trimmed = line.TrimEnd();
            if (trimmed.EndsWith(EndMarker, StringComparison.Ordinal) || trimmed.EndsWith(ErrorMarker, StringComparison.Ordinal))
            {
                break;
            }

            // Skip the echoed prompt+command line.
            if (command is not null && IsPromptEcho(line, command))
            {
                continue;
            }

            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    private static bool IsPromptEcho(string line, string command)
    {
        string trimmed = line.TrimStart('>', ' ');
        return trimmed == command;
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
                _stdin.WriteLine("exit");
                _stdin.Flush();
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
