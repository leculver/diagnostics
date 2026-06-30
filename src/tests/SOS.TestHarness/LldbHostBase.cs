// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace SOS.TestHarness;

/// <summary>
/// Shared machinery for the lldb-CLI hosts (dump and live). Spawns the <c>lldb</c> binary as a child
/// process, imports <c>lldbhelper.py</c> (which adds a <c>runcommand</c> command), and frames every
/// command as <c>runcommand &lt;cmd&gt;</c> so each one is delimited by a sentinel that also carries a real
/// success bit (<c>&lt;END_COMMAND_OUTPUT&gt;</c> / <c>&lt;END_COMMAND_ERROR&gt;</c> from
/// <c>SBCommandReturnObject.Succeeded()</c>). SOS itself is the native lldb plugin
/// (<c>libsosplugin.so</c>/<c>.dylib</c>); its managed extension runs on the runtime named by
/// <c>sethostruntime</c>. Derived hosts differ only in how they create the target (a core file vs. a
/// launched process) and how they advance it.
/// </summary>
public abstract class LldbHostBase : IDebuggerHost
{
    private const string EndMarker = "<END_COMMAND_OUTPUT>";
    private const string ErrorMarker = "<END_COMMAND_ERROR>";

    private static readonly string? s_trace = Environment.GetEnvironmentVariable("SOSHARNESS_LLDB_TRACE");

    private Process _process = null!;
    private StreamWriter _stdin = null!;
    private readonly BlockingCollection<string> _lines = new();
    private Thread _reader = null!;

    public abstract string Name { get; }

    /// <summary>
    /// Spawn lldb, import the command helper, and drain the startup banner so the host is ready for
    /// commands. Derived constructors call this first, then create/advance their target.
    /// <paramref name="configure"/> runs against the <see cref="ProcessStartInfo"/> before launch (e.g. to
    /// set debuggee environment variables a live host needs inherited).
    /// </summary>
    protected void StartLldb(Action<ProcessStartInfo>? configure = null)
    {
        string helper = Path.Combine(AppContext.BaseDirectory, "lldbhelper.py");
        if (!File.Exists(helper))
        {
            throw new FileNotFoundException($"lldb command helper not found at '{helper}'.", helper);
        }

        ProcessStartInfo psi = new()
        {
            FileName = ToolPaths.LldbExe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // --no-lldbinit: ignore the dev's ~/.lldbinit so the session is hermetic.
        // disable-aslr false: toggling ASLR needs ptrace perms we may not have; keep it off so target
        //   creation/launch never fails on that.
        // prompt-on-quit false: never block waiting for a y/n on shutdown.
        psi.ArgumentList.Add("--no-lldbinit");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("settings set target.disable-aslr false");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("settings set interpreter.prompt-on-quit false");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add($"command script import {helper}");

        // Hermetic symbols: scrub any inherited _NT_SYMBOL_PATH (a dev's may point at the Azure-authed
        // symweb). We *remove* it rather than point it at a local cache: the native lldb SOS plugin treats
        // a set _NT_SYMBOL_PATH as the only search root and stops falling back to the on-disk runtime
        // modules, which is how SOS locates the DAC for a locally captured target. Leaving it unset keeps
        // that on-disk resolution working.
        psi.Environment.Remove("_NT_SYMBOL_PATH");

        configure?.Invoke(psi);

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start lldb");
        _stdin = _process.StandardInput;

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "lldb-reader" };
        _reader.Start();

        // Drain the startup banner up to the marker the helper prints from __lldb_init_module.
        DrainToMarker(TimeSpan.FromSeconds(120));
    }

    public abstract void LoadSos();

    /// <summary>Run a raw lldb command verbatim (no SOS dispatch).</summary>
    public SosOutput Execute(string command) => new(Name, command, Run(command));

    /// <summary>Run a SOS command via the plugin's universal <c>sos &lt;command&gt;</c> dispatcher.</summary>
    public SosOutput Sos(string command) => new(Name, command, Run("sos " + command));

    /// <summary>Send a command through the <c>runcommand</c> helper and return its output up to the sentinel.</summary>
    protected string Run(string command)
    {
        _stdin.WriteLine("runcommand " + command);
        _stdin.Flush();
        string outp = DrainToMarker(TimeSpan.FromSeconds(120), command);
        if (s_trace is { Length: > 0 })
        {
            File.AppendAllText(s_trace,
                $"\n>>> lldb={ToolPaths.LldbExe}\n>>> plugin={ToolPaths.LldbPluginPath}\n>>> rt={ToolPaths.HostRuntimeDirectory}\n(lldb) runcommand {command}\n{outp}\n");
        }

        return outp;
    }

    /// <summary>
    /// Collect output lines until the sentinel. Strips lldb's prompt-echo lines (<c>(lldb) ...</c>), which
    /// lldb writes for every command when stdin is redirected; SOS output never begins with that prefix, so
    /// this is safe.
    /// </summary>
    private string DrainToMarker(TimeSpan timeout, string? command = null)
    {
        StringBuilder sb = new();
        while (true)
        {
            if (!_lines.TryTake(out string? line, timeout))
            {
                throw new TimeoutException($"lldb did not return output for '{command ?? "<startup>"}' within {timeout}.");
            }

            string trimmed = line.TrimEnd();
            if (trimmed.EndsWith(EndMarker, StringComparison.Ordinal) || trimmed.EndsWith(ErrorMarker, StringComparison.Ordinal))
            {
                break;
            }

            if (line.StartsWith("(lldb) ", StringComparison.Ordinal))
            {
                continue;
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
                // Ask lldb to quit. A *wedged* lldb (busy-spinning on its inferior, not reading stdin)
                // never sees this, so don't wait long before escalating to a hard kill.
                try
                {
                    _stdin.WriteLine("quit");
                    _stdin.Flush();
                }
                catch
                {
                    // stdin may already be closed; fall through to the kill path.
                }

                if (!_process.WaitForExit(3000))
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
            // Reap the child (and its debuggee, killed via the process tree above). Without this the
            // killed lldb/debuggee linger as unreaped zombies; across a long multi-version run they
            // accumulate, saturate the box, and wedge later live sessions. A bounded wait keeps teardown
            // from blocking if the kill is still propagating.
            try
            {
                _process.WaitForExit(10000);
            }
            catch
            {
                // best effort
            }

            _process.Dispose();
        }
    }
}
