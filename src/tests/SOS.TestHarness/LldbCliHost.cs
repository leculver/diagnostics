// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace SOS.TestHarness;

/// <summary>
/// The dump (post-mortem) "lldb" host (Linux/macOS): opens a core file under <c>lldb</c> and runs SOS
/// against it. The lldb-driving machinery (spawn, <c>runcommand</c> framing, sentinel draining, dispose)
/// lives in <see cref="LldbHostBase"/>; this type only opens the core and points SOS at the right DAC.
///
/// SOS is the native lldb plugin (<c>libsosplugin.so</c>/<c>.dylib</c>), loaded via <c>plugin load</c>;
/// its managed extension is hosted on the runtime named by <c>sethostruntime</c>. SOS commands are
/// dispatched through the plugin's universal <c>sos &lt;command&gt;</c> entry (the lldb analogue of
/// dbgeng's <c>!command</c>), so a test author writes <c>Sos("clrstack")</c> once and it works on every
/// host.
/// </summary>
public sealed class LldbCliHost : LldbHostBase
{
    private readonly Flavor _flavor;

    public override string Name => "lldb";

    public LldbCliHost(string dumpPath, Flavor flavor)
    {
        _flavor = flavor;
        StartLldb();

        // Load the core. SOS is loaded later in LoadSos (the harness calls it after construction).
        Run($"target create --core \"{dumpPath}\"");
    }

    public override void LoadSos()
    {
        Run($"plugin load \"{ToolPaths.LldbPluginPath}\"");
        Run($"sethostruntime \"{ToolPaths.HostRuntimeDirectory}\"");

        // Self-contained single-file bundles carry coreclr inside the exe, so there is no runtime
        // directory on disk next to which SOS can find the matching DAC. Point SOS's symbol store at the
        // runtime pack's native directory (which ships the DAC the publish resolved against); SOS then
        // resolves the DAC for the dump's coreclr build-id from there. This is a *local directory*
        // (no network), so the session stays hermetic. Other flavors find their DAC next to the on-disk
        // runtime and need no override. (cdb does the equivalent via `.cordll -lp`.)
        if (_flavor == Flavor.SingleFile && ToolPaths.SingleFileDacDirectory is { Length: > 0 } dacDir)
        {
            Run($"setsymbolserver -directory \"{dacDir}\"");
        }

        // Local-dev escape hatch (off by default; never set in CI). When a machine has multiple
        // mismatched private runtime builds installed, the bundled cDAC (libmscordaccore_universal)
        // may not match the dump's coreclr and fails to load, which the managed ExtensionCommands
        // surface as "No CLR runtime found". Setting SOSHARNESS_USECDAC=false forces the in-box
        // legacy DAC (selected per the dump's coreclr build), letting the harness be validated end to
        // end on such a machine. This only changes which DAC SOS loads, not any harness behavior.
        if (Environment.GetEnvironmentVariable("SOSHARNESS_USECDAC") is { Length: > 0 } useCDac)
        {
            Run($"runtimes --usecdac {useCDac}");
        }
    }
}
