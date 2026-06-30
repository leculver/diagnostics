// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace SOS.TestHarness;

/// <summary>
/// The live "lldb" host (Linux/macOS): launches the debuggee under <c>lldb</c>, parks it at the program
/// entry (before CoreCLR is up) with SOS loaded, and advances it on demand. It is the lldb analogue of
/// <see cref="DbgEngLiveHost"/>: a stateful, advancing target owned exclusively by one test.
///
/// Unlike the Windows engine — which runs in-process and is therefore driven out-of-process through a
/// child <see cref="ChildEngineClient"/> — lldb is already its own process, so this host drives it
/// directly through <see cref="LldbHostBase"/> (the shared spawn/<c>runcommand</c>/sentinel machinery).
///
/// Stop detection is by text: <c>process continue</c> runs synchronously and its output reports either a
/// stop ("<c>Process N stopped</c>") or an exit ("<c>Process N exited</c>"); the precise managed location
/// is confirmed with <c>clrstack</c>, exactly as the dbgeng host confirms with its own clrstack.
/// </summary>
public sealed class LldbLiveHost : LldbHostBase, ILiveDebuggerHost
{
    private const int MaxResumes = 50;

    private readonly Flavor _flavor;
    private readonly Dac _dac;
    private readonly CoreVersion _coreVersion;

    public override string Name => "lldb-live";

    public LldbLiveHost(string exePath, Flavor flavor, CoreVersion coreVersion = CoreVersion.Net10, Dac dac = Dac.Legacy)
    {
        _flavor = flavor;
        _dac = dac;
        _coreVersion = coreVersion;

        // The debuggee inherits the lldb process environment. Disable W^E so SOS's bpmd can patch JIT-ed
        // code (see dotnet/diagnostics#3126), matching what the legacy live lldb harness set. For a
        // framework-dependent (Core) debuggee, point its apphost at the multi-version test runtime install
        // so it binds the runtime matching its target framework (net8 -> 8.0.x, net11 -> the preview).
        StartLldb(psi =>
        {
            psi.Environment["DOTNET_EnableWriteXorExecute"] = "0";
            if (_flavor == Flavor.Core)
            {
                psi.Environment["DOTNET_ROOT"] = RepoLayout.DotnetTestRoot;
                psi.Environment["DOTNET_ROOT(x86)"] = RepoLayout.DotnetTestRoot;
                psi.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
            }
        });

        Run($"target create \"{exePath}\"");

        // Stop at the program entry so we can load SOS and arm bpmd before the app runs.
        Run("process launch -s");

        // A managed fault (divide-by-zero -> SIGFPE, null-deref -> SIGSEGV, etc.) is first delivered to the
        // runtime's signal handler, which turns it into a managed exception. We must therefore pass those
        // signals through to the debuggee without stopping; an *unhandled* managed exception then tears the
        // process down via abort() (SIGABRT), which is the point a live "run to crash" should stop at.
        Run("process handle -s false -n false -p true SIGFPE");
        Run("process handle -s false -n false -p true SIGSEGV");
        Run("process handle -s true -n true -p true SIGABRT");

        LoadSos();
    }

    public override void LoadSos()
    {
        Run($"plugin load \"{ToolPaths.LldbPluginPath}\"");
        Run($"sethostruntime \"{ToolPaths.HostRuntimeDirectory}\"");

        if (_flavor == Flavor.SingleFile && ToolPaths.SingleFileDacDirectory(_coreVersion) is { Length: > 0 } dacDir)
        {
            Run($"setsymbolserver -directory \"{dacDir}\"");
        }

        // Select the DAC for this config's Dac axis (Legacy => false, CDac on .NET 11+ => true). The
        // SOSHARNESS_USECDAC clamp (off by default; never in CI) overrides it on a skewed dev box.
        Run($"runtimes --usecdac {DacPolicy.UseCDac(_dac)}");
    }

    /// <summary>
    /// Set a managed breakpoint on <paramref name="module"/>!<paramref name="method"/> and run until it is
    /// hit. Throws if the process exits first or the breakpoint is never reached.
    /// </summary>
    public SosOutput RunToBpmd(string module, string method)
    {
        ClearBreakpoints();
        string bpmdOutput = Sos($"bpmd {module} {method}").Text;

        // bpmd reaches the method in stages (a JIT/prestub notification, then the entry), so resume until
        // clrstack confirms we are actually stopped at the requested method.
        for (int i = 0; i < MaxResumes; i++)
        {
            string cont = Execute("process continue").Text;
            if (HasExited(cont))
            {
                throw new InvalidOperationException($"Debuggee exited before hitting bpmd {module}!{method}.");
            }

            if (StoppedAtMethod(method))
            {
                return new SosOutput(Name, $"bpmd {module} {method}", bpmdOutput);
            }
        }

        throw new InvalidOperationException($"Did not reach bpmd {module}!{method} after {MaxResumes} resumes.");
    }

    /// <summary>
    /// Run the process until it crashes (the runtime aborts on an unhandled managed exception, i.e.
    /// SIGABRT). Throws if it exits cleanly without crashing.
    /// </summary>
    public SosOutput RunToCrash()
    {
        ClearBreakpoints();

        for (int i = 0; i < MaxResumes; i++)
        {
            string cont = Execute("process continue").Text;
            if (HasExited(cont))
            {
                throw new InvalidOperationException("Process exited without crashing.");
            }

            if (StoppedOnSignal(cont))
            {
                return new SosOutput(Name, "run-to-crash", cont);
            }
        }

        throw new InvalidOperationException($"Process did not crash after {MaxResumes} resumes.");
    }

    /// <summary>
    /// Resume to the next breakpoint the caller has already armed (e.g. via <c>Sos("bpmd …")</c>). Sets and
    /// clears nothing itself. Throws if the process exits without hitting one.
    /// </summary>
    public SosOutput RunToBreakpoint()
    {
        string cont = Execute("process continue").Text;
        if (HasExited(cont))
        {
            throw new InvalidOperationException("Process exited without hitting a breakpoint.");
        }

        if (StoppedOnSignal(cont))
        {
            throw new InvalidOperationException($"Hit a fatal signal, not a breakpoint:\n{cont}");
        }

        return new SosOutput(Name, "run-to-breakpoint", cont);
    }

    /// <summary>Drop any breakpoints left from a previous stop point so they don't re-trigger on resume.</summary>
    private void ClearBreakpoints()
    {
        Sos("bpmd -clearall");
        Execute("breakpoint delete --force");
    }

    /// <summary>Is the managed call stack currently topped by <paramref name="method"/>?</summary>
    private bool StoppedAtMethod(string method)
    {
        string stack = Sos("clrstack").Text;
        return stack.Contains(method, StringComparison.Ordinal);
    }

    /// <summary>True if a <c>process continue</c> reported the debuggee exiting.</summary>
    private static bool HasExited(string continueOutput) =>
        continueOutput.Contains("exited with status", StringComparison.OrdinalIgnoreCase) ||
        continueOutput.Contains(" exited ", StringComparison.OrdinalIgnoreCase);

    /// <summary>True if a <c>process continue</c> stopped on a signal (the runtime's abort = a crash).</summary>
    private static bool StoppedOnSignal(string continueOutput) =>
        continueOutput.Contains("stop reason = signal", StringComparison.OrdinalIgnoreCase);
}
