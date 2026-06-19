// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace SOS.TestHarness;

/// <summary>
/// Locates the diagnostics repo root and the well-known build output locations the harness
/// consumes (repo-built native SOS, repo-built dotnet-dump, the pre-built debuggees, and the
/// scratch dump directory). The root is found by walking up from the test output directory and
/// looking for the repo markers (<c>global.json</c> alongside <c>Build.cmd</c>), so the harness
/// works regardless of where the test assembly is run from.
/// </summary>
public static class RepoLayout
{
    /// <summary>The build configuration of the repo-built tools (native SOS, dotnet-dump). Defaults
    /// to <c>Debug</c>; set <c>SOSHARNESS_ARTIFACTS_CONFIG=Release</c> to consume Release artifacts.</summary>
    public static string ArtifactsConfiguration { get; } =
        Environment.GetEnvironmentVariable("SOSHARNESS_ARTIFACTS_CONFIG") is { Length: > 0 } c ? c : "Debug";

    /// <summary>The repo root (the directory containing <c>global.json</c> and <c>Build.cmd</c>).</summary>
    public static string Root { get; } = FindRoot();

    /// <summary><c>artifacts/bin</c> under the repo root.</summary>
    public static string ArtifactsBin => Path.Combine(Root, "artifacts", "bin");

    /// <summary>The native build output directory, e.g. <c>artifacts/bin/Windows_NT.x64.Debug</c>.</summary>
    public static string ArtifactsBinNative =>
        Path.Combine(ArtifactsBin, $"{TargetOS}.{TargetArch}.{ArtifactsConfiguration}");

    /// <summary>The processor architecture token used in repo artifact paths (<c>x64</c>/<c>x86</c>/<c>arm64</c>).</summary>
    public static string TargetArch { get; } = RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        _ => "x64",
    };

    /// <summary>The OS token used in repo native artifact paths (currently Windows-only).</summary>
    public static string TargetOS { get; } =
        OperatingSystem.IsWindows() ? "Windows_NT" :
        OperatingSystem.IsMacOS() ? "OSX" : "Linux";

    /// <summary>The runtime identifier the harness builds/publishes against (e.g. <c>win-x64</c>).</summary>
    public static string Rid =>
        (OperatingSystem.IsWindows() ? "win-" : OperatingSystem.IsMacOS() ? "osx-" : "linux-") + TargetArch;

    /// <summary>The repo's locally-acquired .NET host (<c>.dotnet/dotnet.exe</c>) used to shell out builds.</summary>
    public static string DotNetExe => Path.Combine(Root, ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

    /// <summary>Path to a debuggee project under the SOS.UnitTests Debuggees tree.</summary>
    public static string DebuggeeProject(string name) =>
        Path.Combine(Root, "src", "tests", "SOS.UnitTests", "Debuggees", name, name + ".csproj");

    /// <summary>The pre-built Core (net10.0) output directory for a debuggee, as produced by Debuggees.proj.</summary>
    public static string CoreDebuggeeDir(string name) =>
        Path.Combine(ArtifactsBin, name, ArtifactsConfiguration, "net10.0");

    /// <summary>Scratch directory for harness-produced artifacts (on-the-fly builds, captured dumps).</summary>
    public static string Scratch { get; } =
        Path.Combine(Root, "artifacts", "tmp", "sos-harness", ArtifactsConfiguration);

    /// <summary>
    /// A hermetic, local-only symbol path for the SOS host child processes. The dev machine's
    /// <c>_NT_SYMBOL_PATH</c> often points at the Azure-authed <c>symweb</c> server, which makes SOS's
    /// host init pull in Azure.Identity (and fail loading its closure) and would make tests depend on
    /// the network. We point the children at a local cache only — debuggee PDBs are found next to the
    /// module, so managed source/line resolution still works.
    /// </summary>
    public static string SymbolCache { get; } = Path.Combine(Scratch, "symcache");

    private static string FindRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "global.json")) &&
                File.Exists(Path.Combine(dir, "Build.cmd")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            "Could not locate the diagnostics repo root (global.json + Build.cmd) by walking up from " +
            AppContext.BaseDirectory);
    }
}
