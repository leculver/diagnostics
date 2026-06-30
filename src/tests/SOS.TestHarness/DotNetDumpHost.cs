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
    private readonly Flavor _flavor;
    private readonly Dac _dac;
    private readonly CoreVersion _coreVersion;

    public string Name => "dotnet-dump";

    public DotNetDumpHost(string dumpPath, Flavor flavor, Dac dac = Dac.Legacy, CoreVersion coreVersion = CoreVersion.Net10)
    {
        _flavor = flavor;
        _dac = dac;
        _coreVersion = coreVersion;
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

        // Hermetic, local-only symbols (the dev's _NT_SYMBOL_PATH may point at the Azure-authed symweb).
        // NOTE: unlike the cdb/lldb hosts, `dotnet-dump analyze` does NOT honor _NT_SYMBOL_PATH — its
        // Analyzer unconditionally adds the public msdl symbol server on startup (see dotnet-dump
        // Analyzer.cs). We still scrub the env for good measure, but the real seal is the
        // `setsymbolserver -disable` issued in LoadSos below; otherwise a command that resolves symbols
        // (e.g. `!clrstack -gc`) would synchronously download PDBs from msdl and intermittently stall
        // past the harness command timeout, breaking hermeticity and flaking the suite.
        Directory.CreateDirectory(RepoLayout.SymbolCache);
        psi.Environment["_NT_SYMBOL_PATH"] = RepoLayout.SymbolCache;

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

        // Seal the host against the network: `dotnet-dump analyze` auto-adds the public msdl symbol
        // server on startup, so clear it before any command runs. This keeps the suite hermetic and
        // prevents the intermittent multi-minute hang where a symbol-resolving command (e.g.
        // `!clrstack -gc`) blocks on a PDB download from msdl.
        Run("setsymbolserver -disable");

        // Self-contained single-file bundles carry coreclr inside the exe, so there is no runtime
        // directory on disk next to which SOS can find the matching DAC — `analyze` was relying on the
        // (now-disabled) msdl server to download it. Point SOS's symbol store at the runtime pack's
        // native directory (a *local directory*, no network) so it resolves the DAC for the dump's
        // coreclr build locally and the session stays hermetic. Mirrors LldbCliHost. Other flavors find
        // their DAC next to the on-disk runtime and need no override.
        if (_flavor == Flavor.SingleFile && ToolPaths.SingleFileDacDirectory(_coreVersion) is { Length: > 0 } dacDir)
        {
            Run($"setsymbolserver -directory \"{dacDir}\"");
        }

        // Select the DAC for this config's Dac axis: Legacy => `--usecdac false`, CDac (.NET 11+ only) =>
        // `--usecdac true`. The same dump is reused across both, so only this debug-time toggle differs.
        // SOSHARNESS_USECDAC (off by default; never set in CI) is a global clamp that overrides the axis on
        // a dev box whose installed runtimes are skewed such that the cDAC can't load. (Mirrors LldbCliHost.)
        Run($"runtimes --usecdac {DacPolicy.UseCDac(_dac)}");
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
