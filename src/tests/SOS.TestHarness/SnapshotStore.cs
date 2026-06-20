// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;

namespace SOS.TestHarness;

/// <summary>
/// Produces and memoizes the runnable debuggee and the dump for each <c>(flavor, target, stopPoint)</c>.
/// Each <c>(flavor, target)</c> is acquired once and captured once into its own dump directory — so no
/// two tests ever build or write the same artifact.
///
/// Debuggee acquisition follows the repo's build model:
/// <list type="bullet">
///   <item><b>Core (net10.0)</b> is the pre-built debuggee produced by the repo build
///   (<c>Debuggees.proj</c>) under <c>artifacts/bin/&lt;Name&gt;/&lt;Config&gt;/net10.0</c>; the harness
///   consumes it directly (and builds the single project on demand if it isn't there yet).</item>
///   <item><b>Framework (net462)</b> and <b>SingleFile</b> are produced on the fly by building/publishing
///   the repo debuggee csproj (with <c>BuildProjectFramework</c>) into the harness scratch tree, the same
///   way the legacy harness's <c>cli</c> build process does.</item>
/// </list>
///
/// Capture mechanism depends on the flavor and stop kind:
/// <list type="bullet">
///   <item><b>Snapshot stops (Core / SingleFile)</b> self-snapshot mid-run from inside the debuggee via
///   the repo-built <c>dotnet-dump collect</c>.</item>
///   <item><b>Crash stop, Core</b> lets the runtime's createdump write the dump.</item>
///   <item><b>Crash stop, SingleFile</b> can't use createdump, so it's captured with dbgeng like desktop.</item>
///   <item><b>Framework</b> (desktop) is always captured externally by <see cref="DbgEngCapturer"/>.</item>
/// </list>
/// </summary>
public static class SnapshotStore
{
    // One build/publish per (flavor, target) (distinct output dirs); thread-safe via Lazy.
    private static readonly ConcurrentDictionary<(Flavor Flavor, string Target), Lazy<string>> s_targetExe = new();

    // One capture per (flavor, target) (distinct dump dirs); thread-safe via Lazy.
    private static readonly ConcurrentDictionary<(Flavor Flavor, string Target), Lazy<string>> s_captured = new();

    // The out-of-process desktop capturer, located/built once.
    private static readonly Lazy<string> s_capturerDll = new(() => SubprocessDll("SOS.TestHarness.Capturer"));

    private static string CapturerDll => s_capturerDll.Value;

    // The out-of-process dbgeng engine host, located/built once.
    private static readonly Lazy<string> s_engineHostDll = new(() => SubprocessDll("SOS.TestHarness.EngineHost"));

    /// <summary>Path to the built EngineHost.dll (the subprocess dbgeng backend), produced on first use.</summary>
    public static string EngineHostDll => s_engineHostDll.Value;

    /// <summary>Path to the dump for one stop point of a target in a flavor, producing it on first use.</summary>
    public static string GetDump(Flavor flavor, string targetName, string stopName)
    {
        TargetDefinition target = TargetCatalog.Get(targetName);
        target.Stop(stopName); // validate

        string dumpDir = s_captured
            .GetOrAdd((flavor, targetName), key => new Lazy<string>(() => CaptureTarget(key.Flavor, TargetCatalog.Get(key.Target))))
            .Value;

        string dump = Path.Combine(dumpDir, stopName + ".dmp");
        if (!File.Exists(dump))
        {
            throw new InvalidOperationException(
                $"Capture of {flavor}/{targetName} did not produce a dump for stop '{stopName}' at '{dump}'.");
        }

        return dump;
    }

    /// <summary>Path to the runnable executable for a target in a flavor, producing it on first use.</summary>
    public static string TargetExe(Flavor flavor, string targetName) =>
        s_targetExe.GetOrAdd((flavor, targetName), k => new Lazy<string>(() => AcquireTarget(k.Flavor, TargetCatalog.Get(k.Target)))).Value;

    private static string DumpDir(Flavor flavor, string target) =>
        Path.Combine(RepoLayout.Scratch, "dumps", flavor.ToString().ToLowerInvariant(), target);

    private static string CaptureTarget(Flavor flavor, TargetDefinition target)
    {
        string dumpDir = DumpDir(flavor, target.Name);
        Directory.CreateDirectory(dumpDir);

        // Resolve (build if needed) the debuggee first, then reuse the cached dumps only if they were
        // captured from THIS exe (i.e. are at least as new as it). A rebuilt exe has a fresh PDB whose
        // GUID won't match an older dump, so a stale dump must be re-captured.
        string exe = TargetExe(flavor, target.Name);
        DateTime exeTime = File.GetLastWriteTimeUtc(exe);
        if (target.StopPoints.All(s => IsUpToDate(Path.Combine(dumpDir, s.Name + ".dmp"), exeTime)))
        {
            return dumpDir;
        }

        bool isCrash = target.StopPoints.Any(s => s.Kind == StopKind.Crash);

        if (flavor == Flavor.Framework)
        {
            // Desktop: no diagnostics IPC; dbgeng captures both snapshot (bpmd) and crash (second-chance).
            // Run it out-of-process so a dbgeng crash dies with the child, not the test host.
            CaptureWithDbgEng(TargetExe(flavor, target.Name), target, dumpDir);
        }
        else if (isCrash && flavor == Flavor.SingleFile)
        {
            // Self-contained single-file doesn't ship/launch createdump, so capture its crash with
            // dbgeng like desktop (also out-of-process).
            CaptureWithDbgEng(TargetExe(flavor, target.Name), target, dumpDir);
        }
        else if (isCrash)
        {
            // .NET Core crash: let the runtime's createdump write the dump.
            CaptureCrashViaCreatedump(flavor, target, dumpDir);
        }
        else
        {
            // Snapshot stops on Core / SingleFile: self-snapshot mid-run via markers.
            SelfCollectCapture(flavor, target, dumpDir);
        }

        return dumpDir;
    }

    /// <summary>Run the Capturer child to produce dumps via in-process dbgeng (desktop, or single-file crash).</summary>
    private static void CaptureWithDbgEng(string exePath, TargetDefinition target, string dumpDir)
    {
        ProcessStartInfo psi = new(RepoLayout.DotNetExe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(CapturerDll);
        psi.ArgumentList.Add(exePath);
        psi.ArgumentList.Add(target.Name);
        psi.ArgumentList.Add(dumpDir);

        // Hermetic, local-only symbols: the Capturer hosts dbgeng+SOS, and the dev's _NT_SYMBOL_PATH may
        // point at the Azure-authed symweb, which crashes SOS host init (loading Azure.Identity's closure).
        Directory.CreateDirectory(RepoLayout.SymbolCache);
        psi.Environment["_NT_SYMBOL_PATH"] = RepoLayout.SymbolCache;
        ApplyGcMode(psi, target.GcMode);

        using Process p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Capturer");
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"Capturer failed for {target.Name} ({p.ExitCode}):\n{stdout}\n{stderr}");
        }
    }

    /// <summary>
    /// Core/SingleFile crash capture: launch the target with the runtime's crash-dump env vars set
    /// (<c>DOTNET_DbgEnableMiniDump</c> + full dump type) and let it crash. The runtime's createdump
    /// writes the full dump to the crash stop's path; the process exits non-zero (it crashed), so we
    /// verify the dump exists rather than the exit code.
    /// </summary>
    private static void CaptureCrashViaCreatedump(Flavor flavor, TargetDefinition target, string dumpDir)
    {
        string exe = TargetExe(flavor, target.Name);
        StopPoint crash = target.StopPoints.Single(s => s.Kind == StopKind.Crash);
        string dumpPath = Path.Combine(dumpDir, crash.Name + ".dmp");

        ProcessStartInfo psi = new(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.Environment["DOTNET_DbgEnableMiniDump"] = "1";
        psi.Environment["DOTNET_DbgMiniDumpType"] = "4"; // Full — required for SOS/ClrMD and single-file
        psi.Environment["DOTNET_DbgMiniDumpName"] = dumpPath;
        psi.Environment["DOTNET_CreateDumpDiagnostics"] = "1";

        using Process p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch target");
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        if (!File.Exists(dumpPath))
        {
            throw new InvalidOperationException(
                $"createdump did not produce '{dumpPath}' for {target.Project} ({flavor}); exit {p.ExitCode}.\n{stdout}\n{stderr}");
        }
    }

    /// <summary>Core/SingleFile snapshot capture: run the target once; its markers self-snapshot mid-run.</summary>
    /// <summary>Apply the GC-mode env vars to a debuggee launch. Server forces a deterministic multi-heap
    /// GC (a fixed heap count with DATAS off, so it can't collapse back to a single heap).</summary>
    private static void ApplyGcMode(ProcessStartInfo psi, GcMode mode)
    {
        if (mode == GcMode.Server)
        {
            psi.Environment["DOTNET_gcServer"] = "1";
            psi.Environment["DOTNET_GCHeapCount"] = "4";
            psi.Environment["DOTNET_GCDynamicAdaptationMode"] = "0";
        }
    }

    private static void SelfCollectCapture(Flavor flavor, TargetDefinition target, string dumpDir)
    {
        string exe = TargetExe(flavor, target.Name);
        ProcessStartInfo psi = new(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.Environment["SOSHARNESS_CAPTURE_DIR"] = dumpDir;
        // Tell the debuggee's stop-point helper which dotnet-dump to self-collect with (the repo-built one).
        psi.Environment["SOSHARNESS_DOTNET"] = RepoLayout.DotNetExe;
        psi.Environment["SOSHARNESS_DOTNETDUMP_DLL"] = ToolPaths.DotNetDumpDll;
        ApplyGcMode(psi, target.GcMode);

        using Process p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch target");
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"Target '{target.Project}' ({flavor}) failed ({p.ExitCode}):\n{stderr}");
        }
    }

    /// <summary>
    /// Resolve the runnable debuggee for a flavor. Core is the repo's pre-built output; Framework and
    /// SingleFile are built/published on demand from the repo debuggee csproj.
    /// </summary>
    private static string AcquireTarget(Flavor flavor, TargetDefinition target) => flavor switch
    {
        Flavor.Core => AcquireCore(target),
        Flavor.Framework => BuildFlavor(flavor, target),
        Flavor.SingleFile => BuildFlavor(flavor, target),
        _ => throw new ArgumentOutOfRangeException(nameof(flavor)),
    };

    /// <summary>Consume the repo-built Core (net10.0) debuggee; build the single project on demand if it's
    /// absent or older than the debuggee source (so a local debuggee edit is picked up).</summary>
    private static string AcquireCore(TargetDefinition target)
    {
        string exe = Path.Combine(RepoLayout.CoreDebuggeeDir(target.Project), target.Project + ".exe");
        string project = RepoLayout.DebuggeeProject(target.Project);
        if (IsUpToDate(exe, NewestSourceWriteTime(project)))
        {
            return exe;
        }

        // Missing or stale relative to source — build just this debuggee for net10.0 (lands at the same
        // conventional artifacts path).
        lock (BuildLockFor(project))
        {
            if (!IsUpToDate(exe, NewestSourceWriteTime(project)))
            {
                RunToCompletion(RepoLayout.DotNetExe,
                    $"build \"{project}\" -p:BuildProjectFramework=net10.0 -c {RepoLayout.ArtifactsConfiguration}");
            }
        }

        if (!File.Exists(exe))
        {
            throw new InvalidOperationException($"Core build of {target.Project} did not produce '{exe}'.");
        }

        return exe;
    }

    /// <summary>Build (Framework) or publish (SingleFile) the repo debuggee csproj into the scratch tree,
    /// reusing the cached exe when it's newer than the debuggee source.</summary>
    private static string BuildFlavor(Flavor flavor, TargetDefinition target)
    {
        string project = RepoLayout.DebuggeeProject(target.Project);
        string outDir = Path.Combine(RepoLayout.Scratch, "targets", flavor.ToString().ToLowerInvariant(), target.Name);
        string exe = Path.Combine(outDir, target.Project + ".exe");

        if (IsUpToDate(exe, NewestSourceWriteTime(project)))
        {
            return exe;
        }

        string config = RepoLayout.ArtifactsConfiguration;
        string args = flavor switch
        {
            Flavor.Framework =>
                // Desktop SOS resolves source lines from a classic Windows PDB (read via DIA), not a
                // portable/embedded one — the repo's global props default DebugType to embedded, so force
                // a full (Windows) PDB next to the exe for the source-line tests.
                $"build \"{project}\" -p:BuildProjectFramework=net462 -p:DebugType=full -p:DebugSymbols=true -c {config} -o \"{outDir}\"",
            Flavor.SingleFile =>
                $"publish \"{project}\" -p:BuildProjectFramework=net10.0 -r {RepoLayout.Rid} --self-contained true " +
                $"-p:PublishSingleFile=true -c {config} -o \"{outDir}\"",
            _ => throw new ArgumentOutOfRangeException(nameof(flavor)),
        };

        // Rebuild only when stale (above). Different flavors of one csproj share its obj/ (and
        // project.assets.json), so building two flavors concurrently corrupts that shared restore —
        // serialize builds per project.
        lock (BuildLockFor(project))
        {
            if (!IsUpToDate(exe, NewestSourceWriteTime(project)))
            {
                RunToCompletion(RepoLayout.DotNetExe, args);
            }
        }

        if (!File.Exists(exe))
        {
            throw new InvalidOperationException($"Build/publish of {target.Project} ({flavor}) did not produce '{exe}'.");
        }

        return exe;
    }

    private static readonly ConcurrentDictionary<string, object> s_projectBuildLocks = new(StringComparer.OrdinalIgnoreCase);

    private static object BuildLockFor(string projectPath) =>
        s_projectBuildLocks.GetOrAdd(projectPath, _ => new object());

    /// <summary>Newest write time of the debuggee's sources (its <c>.cs</c> files + csproj), so a build is
    /// re-run only when the source actually changed (an unchanged build keeps a stable exe/PDB, which keeps
    /// the cached dumps — captured against that exe's PDB — valid).</summary>
    private static DateTime NewestSourceWriteTime(string projectFile)
    {
        string dir = Path.GetDirectoryName(projectFile)!;
        DateTime newest = File.GetLastWriteTimeUtc(projectFile);
        foreach (string cs in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            DateTime t = File.GetLastWriteTimeUtc(cs);
            if (t > newest)
            {
                newest = t;
            }
        }

        return newest;
    }

    /// <summary>True if <paramref name="output"/> exists and is at least as new as <paramref name="inputUtc"/>.</summary>
    private static bool IsUpToDate(string output, DateTime inputUtc) =>
        File.Exists(output) && File.GetLastWriteTimeUtc(output) >= inputUtc;

    /// <summary>
    /// Build (incrementally) and locate a subprocess host (EngineHost / Capturer). These reference the
    /// harness, so we always run an incremental <c>dotnet build</c> (a no-op when nothing changed) to
    /// guarantee the child never runs a stale copy of the harness; output lands at the conventional
    /// <c>artifacts/bin/&lt;Name&gt;/&lt;Config&gt;/net10.0/&lt;rid&gt;/&lt;Name&gt;.dll</c> path.
    /// </summary>
    private static string SubprocessDll(string name)
    {
        string dll = Path.Combine(RepoLayout.ArtifactsBin, name, RepoLayout.ArtifactsConfiguration, "net10.0", RepoLayout.Rid, name + ".dll");
        string project = Path.Combine(RepoLayout.Root, "src", "tests", name, name + ".csproj");

        lock (BuildLockFor(project))
        {
            RunToCompletion(RepoLayout.DotNetExe, $"build \"{project}\" -c {RepoLayout.ArtifactsConfiguration}");
        }

        if (!File.Exists(dll))
        {
            throw new InvalidOperationException($"Build of {name} did not produce '{dll}'.");
        }

        return dll;
    }

    private static void RunToCompletion(string fileName, string arguments)
    {
        ProcessStartInfo psi = new()
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = RepoLayout.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using Process p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}");
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"'{fileName} {arguments}' failed ({p.ExitCode}):\n{stdout}\n{stderr}");
        }
    }
}
