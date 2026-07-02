// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SOS.TestHarness;
using Xunit;

namespace SOS.Tests;

/// <summary>
/// Central home for baselined, config-specific known issues that must stay visible (a dynamic
/// <see cref="Assert.Skip(string)"/>, never a silent early-return pass) so the affected coverage is
/// re-enabled the moment the underlying defect is fixed. Each guard cites the matching anchor in
/// <c>issues.md</c>. Prefer a real fix over adding a guard here; only baseline genuine, out-of-repo
/// defects (e.g. a dotnet/runtime cDAC bug the harness can't work around).
/// </summary>
internal static class KnownIssues
{
    /// <summary>
    /// Skip a managed stack-walk assertion on a self-contained <b>single-file</b> <b>net11</b> target when
    /// debugging through the <b>cDAC</b>. The universal cDAC (<c>mscordaccore_universal.dll</c>) fails to
    /// start a stack walk on a single-file image (<c>!clrstack</c> prints
    /// <c>Failed to start stack walk: 80131509</c> — COR_E_INVALIDOPERATION). It is host-independent (cdb
    /// and dotnet-dump fail identically), cDAC-only (the legacy DAC walks the same dump fine), and
    /// single-file-only (net11 <em>Core</em> walks fine through the cDAC) — i.e. a dotnet/runtime cDAC
    /// defect the harness can't work around. See issues.md#clrstack-singlefile-net11-cdac.
    /// </summary>
    public static void SkipIfSingleFileNet11CDacStackWalk(TestConfig config)
    {
        if (config.Flavor == Flavor.SingleFile && config.CoreVersion == CoreVersion.Net11 && config.Dac == Dac.CDac)
        {
            Assert.Skip(
                "cDAC (mscordaccore_universal) cannot start a stack walk on a self-contained single-file net11 " +
                "target (0x80131509); see issues.md#clrstack-singlefile-net11-cdac");
        }
    }

    /// <summary>
    /// Skip the <c>!clrthreads</c> thread-state decode on <b>net11</b>. A dotnet/runtime regression
    /// (PR <see href="https://github.com/dotnet/runtime/pull/126592">#126592</see> deleted
    /// <c>threadData->state = thread->m_State;</c> from <c>ClrDataAccess::GetThreadData</c>) leaves the
    /// <c>!clrthreads</c> <b>State</b> column at <c>0</c> for every net11 thread, so there is no non-zero
    /// state value to decode. It affects both DACs (the cDAC's <c>GetThreadData</c> is <c>E_NOTIMPL</c> and
    /// falls back to the same legacy function) and every host — i.e. a runtime-side defect the harness can't
    /// work around. See issues.md#clrthreads-net11.
    /// </summary>
    public static void SkipIfThreadStateNet11(TestConfig config)
    {
        if (config.CoreVersion == CoreVersion.Net11)
        {
            Assert.Skip(
                "net11 !clrthreads State is 0 for all threads (dotnet/runtime #126592 dropped " +
                "DacpThreadData::state), so there is no thread state to decode; see issues.md#clrthreads-net11");
        }
    }
}
