// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace SOS.TestHarness;

/// <summary>
/// Verifies the Windows machine prerequisite for capturing a reduced (Heap/Mini) .NET Core dump of an
/// <b>unsigned</b> test runtime, and fails loudly (with a pointer to the fix) when it is missing.
///
/// <para><b>Why the prerequisite exists.</b> On Windows a reduced dump is written by <c>createdump</c> via
/// <c>MiniDumpWriteDump</c>, which only captures <c>MEM_PRIVATE</c> read/write pages directly. The CLR's
/// loader-allocator heaps — which hold the <c>MethodTable</c>/<c>Module</c> structures SOS and ClrMD need
/// to enumerate modules, types and the GC heap — are <c>MEM_MAPPED</c> (the double-mapped executable
/// allocator), so they are captured only through dbghelp's auxiliary DAC provider (dbghelp loads
/// <c>mscordaccore.dll</c> and calls <c>ICLRDataEnumMemoryRegions::EnumMemoryRegions</c>). dbghelp refuses
/// to load a DAC that isn't Authenticode-signed unless
/// <c>HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\MiniDumpSettings\DisableAuxProviderSignatureCheck</c>
/// is set to 1. The locally-built and preview (net11) test runtimes ship an <b>unsigned</b> DAC, so without
/// that value their loader heaps are silently omitted from a reduced dump and every module/type/heap
/// command then fails with "Unable to create a ClrHeap …". (Released runtimes such as net8–net10 have a
/// signed DAC; desktop Framework and single-file/Full captures do not use this path.)</para>
///
/// <para><b>Why we only check, never set.</b> That value lives under <c>HKLM</c>, so writing it requires
/// elevation — and the tests are not expected to run as administrator. Setting it is a one-time, explicit
/// developer/CI step performed by <c>eng\DisableSignatureCheck.ps1</c> (with <c>-Restore</c> to undo). This
/// type therefore reads the value (once, cached) and, if a capture that depends on it is attempted while it
/// is unset, throws a clear failure pointing at that script rather than producing a silently-incomplete
/// dump. It never modifies the registry.</para>
/// </summary>
internal static class DumpGenerationRequirements
{
    private static readonly string s_root = RuntimeInformation.ProcessArchitecture == Architecture.X86 ? @"SOFTWARE\WOW6432Node\" : @"SOFTWARE\";
    private static readonly string s_settingsNode = s_root + @"Microsoft\Windows NT\CurrentVersion\MiniDumpSettings";
    private const string DisableCheckValue = "DisableAuxProviderSignatureCheck";

    // Read the registry value at most once per process (cheap, read-only; reading HKLM needs no elevation).
    private static readonly Lazy<bool> s_signatureCheckDisabled = new(ReadSignatureCheckDisabled);

    /// <summary>
    /// Throws when a capture that needs the aux-DAC-provider signature-check bypass is about to run on
    /// Windows while the bypass is not enabled. Only a <b>reduced</b> (Heap/Mini) <b>Core</b> dump goes
    /// through that path: Full dumps capture all memory directly, single-file snapshots are always collected
    /// Full, and desktop Framework is captured via dbgeng using the signed in-box DAC — none of those need
    /// the bypass, so this is a no-op for them (and on non-Windows).
    /// </summary>
    internal static void EnsureAvailableFor(Flavor flavor, DumpKind dumpKind)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        if (dumpKind == DumpKind.Full || flavor == Flavor.Framework || flavor == Flavor.SingleFile)
        {
            return;
        }

        if (s_signatureCheckDisabled.Value)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Cannot capture a reduced ({dumpKind}) .NET Core dump: the machine registry value " +
            $@"'HKLM\{s_settingsNode}\{DisableCheckValue}' is not set to 1. Without it dbghelp will not load " +
            "the unsigned test DAC, so the dump is missing the CLR loader heaps and every SOS/ClrMD " +
            "module/type/heap command fails with 'Unable to create a ClrHeap …'. This value requires " +
            "administrator rights to set, so the tests do not set it themselves. Enable it once from an " +
            @"elevated PowerShell by running 'eng\DisableSignatureCheck.ps1 -RepoRoot " + RepoLayout.Root +
            @"' (and 'eng\DisableSignatureCheck.ps1 -Restore -RepoRoot " + RepoLayout.Root + "' to undo). " +
            "See documentation/privatebuildtesting.md.");
    }

    private static bool ReadSignatureCheckDisabled()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return true;
        }

        return ReadSignatureCheckDisabledWindows();
    }

    [SupportedOSPlatform("windows")]
    private static bool ReadSignatureCheckDisabledWindows()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(s_settingsNode);
            return key?.GetValue(DisableCheckValue) is int value && value == 1;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
