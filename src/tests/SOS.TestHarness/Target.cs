// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace SOS.TestHarness;

/// <summary>
/// A debug target a test drives, obtained from <see cref="Targets.GetTargetAsync"/>. There is one
/// surface for both worlds; only the semantics of navigation differ:
/// <list type="bullet">
///   <item><b>Dead</b> (<c>live: false</c>, <see cref="DeadTarget"/>) is backed by immutable cached
///   dumps. <see cref="Sos"/> throws until you navigate to a point; after that you may
///   <see cref="GoToStopPoint"/>/<see cref="GoToCrash"/> among points in any order, repeatedly.</item>
///   <item><b>Live</b> (<c>live: true</c>, <see cref="LiveTarget"/>) is a single advancing process,
///   parked at the debugger's initial breakpoint (before CoreCLR loads). <see cref="Sos"/> already
///   works there (e.g. <c>bpmd</c>); navigation only moves forward.</item>
/// </list>
/// </summary>
public abstract class Target : IDisposable
{
    protected Target(Host host, string targetName, Flavor flavor)
    {
        Host = host;
        TargetName = targetName;
        Flavor = flavor;
    }

    public Host Host { get; }
    public string TargetName { get; }
    public Flavor Flavor { get; }

    /// <summary>The dump file currently backing this target (dead targets only).</summary>
    public virtual string DumpPath =>
        throw new NotSupportedException("A live target has no dump file.");

    /// <summary>Navigate to the named stop point. Dead: load that point's dump. Live: run forward to its marker.</summary>
    public void GoToStopPoint(string stopName)
    {
        GoToStopPointCore(stopName);
        ReplayContext.Current?.Add(ReplayStepKind.Navigate, $"GoToStopPoint(\"{stopName}\")", SafeDumpPath());
    }

    /// <summary>Navigate to the target's crash. Dead: load the crash dump. Live: run forward to the crash.</summary>
    public void GoToCrash()
    {
        GoToCrashCore();
        ReplayContext.Current?.Add(ReplayStepKind.Navigate, "GoToCrash()", SafeDumpPath());
    }

    /// <summary>Run a SOS command at the current point.</summary>
    public SosOutput Sos(string command)
    {
        // Record before running so a throwing command is still captured; the dump (if any) is the one
        // we're currently parked on, which is exactly what the command runs against.
        ReplayContext.Current?.Add(ReplayStepKind.Sos, command, SafeDumpPath());
        return SosCore(command);
    }

    /// <summary>Run a raw debugger command at the current point.</summary>
    public SosOutput Execute(string command)
    {
        ReplayContext.Current?.Add(ReplayStepKind.Execute, command, SafeDumpPath());
        return ExecuteCore(command);
    }

    /// <summary>Navigate to the named stop point (subclass mechanics; <see cref="GoToStopPoint"/> records it).</summary>
    protected abstract void GoToStopPointCore(string stopName);

    /// <summary>Navigate to the crash (subclass mechanics; <see cref="GoToCrash"/> records it).</summary>
    protected abstract void GoToCrashCore();

    /// <summary>Run a SOS command (subclass mechanics; <see cref="Sos"/> records it).</summary>
    protected abstract SosOutput SosCore(string command);

    /// <summary>Run a raw debugger command (subclass mechanics; <see cref="Execute"/> records it).</summary>
    protected abstract SosOutput ExecuteCore(string command);

    /// <summary>The dump backing this target now, or null if it has none yet / is a live target.</summary>
    private protected string? SafeDumpPath()
    {
        try
        {
            return DumpPath;
        }
        catch
        {
            return null;
        }
    }

    public virtual void Dispose()
    {
    }
}
