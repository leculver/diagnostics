// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace SOS.TestHarness;

/// <summary>
/// Resolves the external tools the harness needs, preferring fully self-contained
/// sources (restored NuGet packages, the per-user SOS install) over machine-wide
/// debugger installs. Everything here is discovered at runtime so the PoC "just runs"
/// after <c>dotnet sos install</c> and a normal restore.
/// </summary>
public static class ToolPaths
{
    /// <summary>
    /// Directory containing <c>dbgeng.dll</c> (+ dbghelp/dbgcore/dbgmodel/msdia), taken from
    /// the restored <c>Microsoft.Debugging.Platform.DbgEng</c> package. No WinDbg install needed.
    /// </summary>
    public static string DbgEngDirectory { get; } = ResolveDbgEngDirectory();

    /// <summary>Native SOS (<c>sos.dll</c>) from the per-user install created by <c>dotnet sos install</c>.</summary>
    public static string SosPath { get; } = ResolveSosPath();

    /// <summary>The <c>dotnet-dump</c> global tool executable.</summary>
    public static string DotNetDumpExe { get; } = ResolveDotNetDumpExe();

    private static string ResolveDbgEngDirectory()
    {
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "amd64",
        };

        foreach (string root in NuGetPackageRoots())
        {
            string pkg = Path.Combine(root, "microsoft.debugging.platform.dbgeng");
            if (!Directory.Exists(pkg))
            {
                continue;
            }

            // Prefer the highest version present.
            foreach (string versionDir in Directory.GetDirectories(pkg).OrderByDescending(d => d))
            {
                string content = Path.Combine(versionDir, "content", arch);
                if (File.Exists(Path.Combine(content, "dbgeng.dll")))
                {
                    return content;
                }
            }
        }

        throw new FileNotFoundException(
            "Could not locate dbgeng.dll from the Microsoft.Debugging.Platform.DbgEng package. " +
            "Run 'dotnet restore' on the harness project.");
    }

    private static string ResolveSosPath()
    {
        string path = Path.Combine(UserProfile, ".dotnet", "sos", "sos.dll");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Native SOS not found at '{path}'. Run 'dotnet sos install' to deploy it.", path);
        }

        return path;
    }

    private static string ResolveDotNetDumpExe()
    {
        string exe = OperatingSystem.IsWindows() ? "dotnet-dump.exe" : "dotnet-dump";
        string path = Path.Combine(UserProfile, ".dotnet", "tools", exe);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"dotnet-dump not found at '{path}'. Install with 'dotnet tool install -g dotnet-dump'.", path);
        }

        return path;
    }

    private static IEnumerable<string> NuGetPackageRoots()
    {
        string? env = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrEmpty(env))
        {
            yield return env;
        }

        yield return Path.Combine(UserProfile, ".nuget", "packages");
        yield return @"C:\Nuget";
    }

    private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
