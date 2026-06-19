// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace SOS.TestHarness;

/// <summary>
/// Resolves the external tools the harness drives, pointing every one at the diagnostics repo's
/// own build outputs (so the harness always validates freshly-built SOS, not a stale machine-wide
/// install):
/// <list type="bullet">
///   <item><c>dbgeng.dll</c> comes from the restored <c>cdb-sos</c> NuGet package (the same one the
///   legacy <c>SOS.UnitTests</c> uses) — no WinDbg install needed.</item>
///   <item>Native <c>sos.dll</c> comes from the repo's native build output.</item>
///   <item><c>dotnet-dump</c> is the repo-built tool, run as <c>dotnet dotnet-dump.dll</c>.</item>
/// </list>
/// </summary>
public static class ToolPaths
{
    /// <summary>
    /// Directory containing <c>dbgeng.dll</c> (+ dbghelp/dbgcore/dbgmodel/symsrv), taken from the
    /// restored <c>cdb-sos</c> package at
    /// <c>&lt;pkgRoot&gt;/cdb-sos/&lt;ver&gt;/runtimes/win-&lt;arch&gt;/native</c>.
    /// </summary>
    public static string DbgEngDirectory { get; } = ResolveDbgEngDirectory();

    /// <summary>Repo-built native SOS (<c>sos.dll</c>) from <see cref="RepoLayout.ArtifactsBinNative"/>.</summary>
    public static string SosPath { get; } = ResolveSosPath();

    /// <summary>Repo-built <c>dotnet-dump</c> managed entry point, run as <c>dotnet &lt;dll&gt;</c>.</summary>
    public static string DotNetDumpDll { get; } = ResolveDotNetDumpDll();

    private static string ResolveDbgEngDirectory()
    {
        string relativeNative = Path.Combine("runtimes", $"win-{RepoLayout.TargetArch}", "native");

        foreach (string root in NuGetPackageRoots())
        {
            string pkg = Path.Combine(root, "cdb-sos");
            if (!Directory.Exists(pkg))
            {
                continue;
            }

            // Prefer the highest version present.
            foreach (string versionDir in Directory.GetDirectories(pkg).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string native = Path.Combine(versionDir, relativeNative);
                if (File.Exists(Path.Combine(native, "dbgeng.dll")))
                {
                    return native;
                }
            }
        }

        throw new FileNotFoundException(
            "Could not locate dbgeng.dll from the cdb-sos package. Restore SOS.UnitTests (or the harness " +
            "test project) so the cdb-sos PackageDownload populates the NuGet cache.");
    }

    private static string ResolveSosPath()
    {
        string path = Path.Combine(RepoLayout.ArtifactsBinNative, "sos.dll");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Repo-built native SOS not found at '{path}'. Build the repo (Build.cmd) so the native " +
                "SOS is produced for this configuration/architecture.", path);
        }

        return path;
    }

    private static string ResolveDotNetDumpDll()
    {
        // dotnet-dump targets net8.0 and is published under the repo artifacts; prefer the published
        // copy (self-contained closure) and fall back to the plain build output.
        string baseDir = Path.Combine(RepoLayout.ArtifactsBin, "dotnet-dump", RepoLayout.ArtifactsConfiguration, "net8.0");
        string published = Path.Combine(baseDir, "publish", "dotnet-dump.dll");
        if (File.Exists(published))
        {
            return published;
        }

        string built = Path.Combine(baseDir, "dotnet-dump.dll");
        if (File.Exists(built))
        {
            return built;
        }

        throw new FileNotFoundException(
            $"Repo-built dotnet-dump not found under '{baseDir}'. Build the repo (Build.cmd) so dotnet-dump " +
            "is produced.", published);
    }

    private static IEnumerable<string> NuGetPackageRoots()
    {
        string? env = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrEmpty(env))
        {
            yield return env;
        }

        yield return Path.Combine(UserProfile, ".nuget", "packages");
    }

    private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
