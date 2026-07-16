// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SOS.TestHarness;

/// <summary>
/// Registers the Windows dump-generation registry keys once per test process so that reduced
/// (Heap/Mini/Triage) dumps of the local test runtimes are complete.
///
/// <para><b>Why this is required.</b> On Windows a reduced dump is written by <c>createdump</c> via
/// <c>MiniDumpWriteDump</c>, which only captures <c>MEM_PRIVATE</c> read/write pages directly. The CLR's
/// loader-allocator heaps — which hold the <c>MethodTable</c>/<c>Module</c> structures SOS and ClrMD need
/// to enumerate modules, types and the GC heap — are <c>MEM_MAPPED</c> (the double-mapped executable
/// allocator), so they are captured only through dbghelp's auxiliary DAC provider: dbghelp loads
/// <c>mscordaccore.dll</c> and calls <c>ICLRDataEnumMemoryRegions::EnumMemoryRegions</c> to add those
/// regions. dbghelp refuses to load a DAC that isn't Authenticode-signed unless
/// <c>DisableAuxProviderSignatureCheck</c> is set (or the DAC is registered in
/// <c>KnownManagedDebuggingDlls</c>). The locally-built and preview (net11) test runtimes ship an
/// <b>unsigned</b> DAC, so without these keys their loader heaps are silently omitted and every
/// module/type/heap command fails against the reduced dump. (Released runtimes such as net8–net10 have a
/// signed DAC and are unaffected, which is why only the preview versions regress.)
///
/// <para>This mirrors the legacy <c>SOS.UnitTests</c> <c>DumpGenerationSetup</c>. Writing to
/// <c>HKLM</c> requires elevation; when the process is not elevated the
/// <see cref="UnauthorizedAccessException"/> is swallowed and the keys are simply not written (matching
/// CI, which runs elevated / pre-provisions the keys via <c>eng/DisableSignatureCheck.ps1</c>).
///
/// <para><see cref="EnsureConfigured"/> is invoked (once, thread-safe) before the first dump is captured.
/// </summary>
internal static class DumpGenerationSetup
{
    private static readonly string s_root = RuntimeInformation.ProcessArchitecture == Architecture.X86 ? @"SOFTWARE\WOW6432Node\" : @"SOFTWARE\";
    private static readonly string s_nodePath = s_root + @"Microsoft\Windows NT\CurrentVersion\";
    private static readonly string s_auxiliaryNode = s_nodePath + "MiniDumpAuxiliaryDlls";
    private static readonly string s_knownNode = s_nodePath + "KnownManagedDebuggingDlls";
    private static readonly string s_settingsNode = s_nodePath + "MiniDumpSettings";
    private const string DisableCheckValue = "DisableAuxProviderSignatureCheck";

    private static HashSet<string>? s_paths;

    // Runs the registration exactly once per process, the first time a dump is about to be captured.
    private static readonly Lazy<bool> s_configured = new(Configure);

    /// <summary>Ensures the Windows dump-generation registry keys are registered (idempotent, thread-safe).</summary>
    internal static void EnsureConfigured() => _ = s_configured.Value;

    private static bool Configure()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        // Create the key for the newer Windows (11 or greater) that gates the aux provider on a signature.
        try
        {
            using RegistryKey settingsKey = Registry.LocalMachine.CreateSubKey(s_settingsNode, writable: true);
            settingsKey.SetValue(DisableCheckValue, 1, RegistryValueKind.DWord);
        }
        catch (UnauthorizedAccessException)
        {
        }

        // The DACs to trust are the ones next to each installed test runtime's coreclr.dll
        // (artifacts/dotnet-test/shared/Microsoft.NETCore.App/<version>/).
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        string sharedFxRoot = Path.Combine(RepoLayout.DotnetTestRoot, "shared", "Microsoft.NETCore.App");
        try
        {
            foreach (string directory in Directory.GetDirectories(sharedFxRoot))
            {
                if (File.Exists(Path.Combine(directory, "mscordaccore.dll")))
                {
                    paths.Add(directory);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        if (paths.Count > 0)
        {
            // Register each runtime's DAC as a known/auxiliary debugging DLL so dbghelp will load it
            // (used by older Windows and as a belt-and-suspenders alongside the settings key above).
            try
            {
                using RegistryKey auxiliaryKey = Registry.LocalMachine.CreateSubKey(s_auxiliaryNode, writable: true);
                using RegistryKey knownKey = Registry.LocalMachine.CreateSubKey(s_knownNode, writable: true);

                foreach (string path in paths)
                {
                    string dacPath = Path.Combine(path, "mscordaccore.dll");
                    string runtimePath = Path.Combine(path, "coreclr.dll");
                    knownKey.SetValue(dacPath, 0, RegistryValueKind.DWord);
                    auxiliaryKey.SetValue(runtimePath, dacPath, RegistryValueKind.String);
                }

                // Save the paths only after writing them successfully so cleanup removes exactly what we added.
                s_paths = paths;
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        AppDomain.CurrentDomain.ProcessExit += Cleanup;
        return true;
    }

    private static void Cleanup(object? sender, EventArgs e)
    {
        if (s_paths is null || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            HashSet<string> paths = s_paths;
            s_paths = null;

            using RegistryKey auxiliaryKey = Registry.LocalMachine.CreateSubKey(s_auxiliaryNode, writable: true);
            using RegistryKey knownKey = Registry.LocalMachine.CreateSubKey(s_knownNode, writable: true);

            foreach (string path in paths)
            {
                string dacPath = Path.Combine(path, "mscordaccore.dll");
                string runtimePath = Path.Combine(path, "coreclr.dll");
                knownKey.DeleteValue(dacPath, throwOnMissingValue: false);
                auxiliaryKey.DeleteValue(runtimePath, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
        }
    }
}
