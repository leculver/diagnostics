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

    /// <summary>
    /// Directory containing the <c>mscordaccore.dll</c> (DAC) that matches the runtime a self-contained
    /// single-file debuggee bundles. Self-contained single-file apps carry the runtime inside the exe,
    /// so dbgeng can't find the DAC next to a runtime on disk and (hermetically) can't download it; we
    /// load it explicitly via <c>.cordll -lp</c>. The version is the repo's pinned net10 runtime
    /// (<c>MicrosoftNETCoreApp100Version</c>), which the publish resolves against, and the DAC ships in
    /// that runtime pack. Returns <c>null</c> if it can't be located.
    /// </summary>
    public static string? SingleFileDacDirectory { get; } = ResolveSingleFileDacDirectory();

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

    private static string? ResolveSingleFileDacDirectory()
    {
        string rid = RepoLayout.Rid; // win-x64 / win-arm64 / ...
        string packId = $"microsoft.netcore.app.runtime.{rid}";
        string relativeNative = Path.Combine("runtimes", rid, "native");

        // Preferred: the repo's pinned net10 runtime version (what the self-contained single-file
        // publish resolves against), read straight from eng/Versions.props.
        string? pinned = ReadVersionsProp("MicrosoftNETCoreApp100Version");
        if (!string.IsNullOrEmpty(pinned))
        {
            foreach (string root in NuGetPackageRoots())
            {
                string native = Path.Combine(root, packId, pinned!, relativeNative);
                if (File.Exists(Path.Combine(native, "mscordaccore.dll")))
                {
                    return native;
                }
            }
        }

        // Fallback: the highest net10 runtime pack present.
        foreach (string root in NuGetPackageRoots())
        {
            string pkg = Path.Combine(root, packId);
            if (!Directory.Exists(pkg))
            {
                continue;
            }

            string? best = Directory.GetDirectories(pkg)
                .Select(Path.GetFileName)
                .Where(v => v is not null && v.StartsWith("10.0.", StringComparison.Ordinal))
                .OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (best is not null)
            {
                string native = Path.Combine(pkg, best, relativeNative);
                if (File.Exists(Path.Combine(native, "mscordaccore.dll")))
                {
                    return native;
                }
            }
        }

        return null;
    }

    private static string? ReadVersionsProp(string name)
    {
        string versionsProps = Path.Combine(RepoLayout.Root, "eng", "Versions.props");
        if (!File.Exists(versionsProps))
        {
            return null;
        }

        foreach (string line in File.ReadLines(versionsProps))
        {
            int open = line.IndexOf($"<{name}>", StringComparison.Ordinal);
            if (open < 0)
            {
                continue;
            }

            open += name.Length + 2;
            int close = line.IndexOf($"</{name}>", open, StringComparison.Ordinal);
            if (close > open)
            {
                return line.Substring(open, close - open).Trim();
            }
        }

        return null;
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
