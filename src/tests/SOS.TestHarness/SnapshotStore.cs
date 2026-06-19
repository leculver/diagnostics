// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;

namespace SOS.TestHarness;

/// <summary>
/// Produces and memoizes the dump for each <c>(flavor, target, stopPoint)</c>. Each
/// <c>(flavor, target)</c> is built/published once into its own directory and captured once into
/// its own dump directory — so no two tests ever build or write the same artifact, and nothing is
/// regenerated per dump variant (the failure mode behind clrmd#1483).
///
/// Capture mechanism depends on the flavor and stop kind:
/// <list type="bullet">
///   <item><b>Snapshot stops (Core / SingleFile)</b> self-snapshot mid-run from inside the
///   debuggee via <c>dotnet-dump collect</c>.</item>
///   <item><b>Crash stop, Core</b> lets the runtime's createdump write the dump
///   (<c>DOTNET_DbgEnableMiniDump</c>).</item>
///   <item><b>Crash stop, SingleFile</b> can't use createdump (the self-contained single-file
///   bundle doesn't ship/launch it), so it's captured with dbgeng like desktop.</item>
///   <item><b>Framework</b> (desktop, no diagnostics IPC) is always captured externally by
///   <see cref="DbgEngCapturer"/> driving in-process dbgeng.</item>
/// </list>
/// </summary>
public static class SnapshotStore
{
    // The build configuration for the debuggee targets. Default "Debug" (unoptimized); set
    // SOSHARNESS_TARGET_CONFIGURATION=Release to build optimized debuggees (the Release-mode test
    // pass). Only affects the test targets — the harness infra (EngineHost/Capturer) stays Debug.
    private static readonly string s_targetConfiguration =
        Environment.GetEnvironmentVariable("SOSHARNESS_TARGET_CONFIGURATION") is { Length: > 0 } c ? c : "Debug";

    // One build/publish per (flavor, target) (distinct output dirs); thread-safe via Lazy.
    private static readonly ConcurrentDictionary<(Flavor Flavor, string Target), Lazy<string>> s_targetExe = new();

    // One capture per (flavor, target) (distinct dump dirs); thread-safe via Lazy.
    private static readonly ConcurrentDictionary<(Flavor Flavor, string Target), Lazy<string>> s_captured = new();

    // The out-of-process desktop capturer, built once.
    private static readonly Lazy<string> s_capturerExe = new(BuildCapturer);

    private static string CapturerExe => s_capturerExe.Value;

    // The out-of-process dbgeng engine host, built once.
    private static readonly Lazy<string> s_engineHostDll = new(BuildEngineHost);

    /// <summary>Path to the built EngineHost.dll (the subprocess dbgeng backend), built on first use.</summary>
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

    /// <summary>Path to the built/published executable for a target in a flavor, producing it on first use.</summary>
    public static string TargetExe(Flavor flavor, string targetName) =>
        s_targetExe.GetOrAdd((flavor, targetName), k => new Lazy<string>(() => BuildTarget(k.Flavor, TargetCatalog.Get(k.Target)))).Value;

    private static string DumpDir(Flavor flavor, string target) =>
        Path.Combine(PocLayout.Root, "artifacts", "dumps", s_targetConfiguration.ToLowerInvariant(), flavor.ToString().ToLowerInvariant(), target);

    private static string CaptureTarget(Flavor flavor, TargetDefinition target)
    {
        string dumpDir = DumpDir(flavor, target.Name);
        Directory.CreateDirectory(dumpDir);

        if (target.StopPoints.All(s => File.Exists(Path.Combine(dumpDir, s.Name + ".dmp"))))
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

    /// <summary>Run the Capturer child exe to produce dumps via in-process dbgeng (desktop, or single-file crash).</summary>
    private static void CaptureWithDbgEng(string exePath, TargetDefinition target, string dumpDir)
    {
        string capturer = CapturerExe;
        ProcessStartInfo psi = new("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(capturer);
        psi.ArgumentList.Add(exePath);
        psi.ArgumentList.Add(target.Name);
        psi.ArgumentList.Add(dumpDir);

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

        using Process p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch target");
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"Target '{target.Project}' ({flavor}) failed ({p.ExitCode}):\n{stderr}");
        }
    }

    private static string BuildTarget(Flavor flavor, TargetDefinition target)
    {
        string root = PocLayout.Root;
        string project = Path.Combine(root, "testtargets", target.Project, target.Project + ".csproj");
        string outDir = Path.Combine(root, "artifacts", "targets", s_targetConfiguration.ToLowerInvariant(), flavor.ToString().ToLowerInvariant(), target.Name);
        string exe = Path.Combine(outDir, target.Project + ".exe");

        if (File.Exists(exe))
        {
            return exe;
        }

        string config = s_targetConfiguration;
        string args = flavor switch
        {
            Flavor.Core => $"build \"{project}\" -c {config} -f net10.0 -o \"{outDir}\"",
            Flavor.Framework => $"build \"{project}\" -c {config} -f net48 -o \"{outDir}\"",
            Flavor.SingleFile =>
                $"publish \"{project}\" -c {config} -f net10.0 -r {Rid} --self-contained true -p:PublishSingleFile=true -o \"{outDir}\"",
            _ => throw new ArgumentOutOfRangeException(nameof(flavor)),
        };

        // Different flavors of one csproj share its obj/ (and project.assets.json). Building two
        // flavors concurrently corrupts that shared restore — e.g. a no-RID Core restore racing a
        // win-x64 SingleFile publish yields NETSDK1047. Serialize builds per project so each flavor's
        // restore completes before the next begins.
        lock (BuildLockFor(project))
        {
            if (File.Exists(exe))
            {
                return exe;
            }

            RunToCompletion("dotnet", args, root);
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

    private static string Rid => OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsMacOS() ? "osx-x64" : "linux-x64";

    private static string BuildCapturer()
    {
        string root = PocLayout.Root;
        string project = Path.Combine(root, "src", "Capturer", "Capturer.csproj");
        string outDir = Path.Combine(root, "artifacts", "capturer");
        string dll = Path.Combine(outDir, "Capturer.dll");

        // Always build (incrementally): Capturer references SOS.TestHarness, so a stale cached copy
        // would silently run old harness code. dotnet build is a no-op when nothing changed.
        RunToCompletion("dotnet", $"build \"{project}\" -c Debug -o \"{outDir}\"", root);

        if (!File.Exists(dll))
        {
            throw new InvalidOperationException($"Capturer build did not produce '{dll}'.");
        }

        return dll;
    }

    private static string BuildEngineHost()
    {
        string root = PocLayout.Root;
        string project = Path.Combine(root, "src", "EngineHost", "EngineHost.csproj");
        string outDir = Path.Combine(root, "artifacts", "enginehost");
        string dll = Path.Combine(outDir, "EngineHost.dll");

        // Always build (incrementally): EngineHost references SOS.TestHarness, so a stale cached copy
        // would silently run old harness code. dotnet build is a no-op when nothing changed.
        RunToCompletion("dotnet", $"build \"{project}\" -c Debug -o \"{outDir}\"", root);

        if (!File.Exists(dll))
        {
            throw new InvalidOperationException($"EngineHost build did not produce '{dll}'.");
        }

        return dll;
    }

    private static void RunToCompletion(string fileName, string arguments, string workingDir)
    {
        ProcessStartInfo psi = new()
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir,
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
