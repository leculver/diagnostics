// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SOS.TestHarness;
using Xunit;

namespace SOS.Tests;

/// <summary>
/// One home for every "this combination is a documented known issue, skip it" decision, so the
/// condition that triggers the skip, the human reason, and the <c>issues.md</c> anchor live together
/// and are greppable. Each member names a specific known issue; add a new member here rather than
/// inlining an <see cref="Assert.SkipWhen"/> with a raw "see issues.md#…" string at the call site.
/// xUnit's dynamic skip works through a thrown exception, so calling these from a test (or a test
/// helper) registers the skip just as an inline <c>Assert.SkipWhen</c> would.
///
/// Host-structural <em>live</em> limitations that apply uniformly to every test exercising a (host,
/// flavor) combination — with no per-test variation to express — are instead enforced once in the
/// harness (see <c>LiveTarget</c>, which throws a dynamic skip for live <c>bpmd</c> on a single-file
/// target under lldb; issues.md#bpmd-singlefile-live-lldb) rather than being repeated at dozens of call
/// sites.
/// </summary>
internal static class KnownIssues
{
    /// <summary>
    /// ICorDebug cannot retrieve local variables from a single-file bundle — they come back as
    /// anonymous <c>local_N</c> errors. Parameters still resolve, so only the locals assertions skip.
    /// See issues.md#clrstack-i-singlefile.
    /// </summary>
    public static void SkipIcorDebugLocalsOnSingleFile(Flavor flavor) =>
        Assert.SkipWhen(flavor == Flavor.SingleFile,
            "ICorDebug cannot retrieve locals on single-file; see issues.md#clrstack-i-singlefile");

    /// <summary>
    /// Driving the Scenarios debuggee live through the generation-promotion markers (a <c>bpmd</c> on
    /// each of AtGen0/1/2 with a blocking <c>GC.Collect(2)</c> between them) crashes the debuggee with an
    /// internal CLR error under live dbgeng. The dump path exercises the same generation progression, so
    /// gcwhere's "object moves across generations" check runs there. See issues.md#gcwhere-live-gc.
    /// </summary>
    public static void SkipLiveGenPromotion(Liveness liveness) =>
        Assert.SkipWhen(liveness == Liveness.Live,
            "live gen-promotion bpmd + GC.Collect(2) crashes the debuggee under dbgeng; see issues.md#gcwhere-live-gc");

    /// <summary>
    /// <c>clrma</c> drives the native CLRMA managed-analysis provider, which is only surfaced by the dbgeng
    /// (cdb) and managed (dotnet-dump) hosts. The lldb SOS plugin does not expose it, so <c>sos clrma</c>
    /// comes back as an unrecognized command. See issues.md#clrma-lldb.
    /// </summary>
    public static void SkipClrmaOnLldb(Host host) =>
        Assert.SkipWhen(host == Host.Lldb,
            "clrma is not surfaced by the lldb SOS plugin; see issues.md#clrma-lldb");

    /// <summary>
    /// <c>gchandleleaks</c> is a Windows-only SOS command (gated <c>#ifndef FEATURE_PAL</c>, registered only
    /// by WindowsSOSCommand and absent from the Unix SOS exports), so it is unavailable on every non-Windows
    /// host (lldb and dotnet-dump alike). See issues.md#gchandleleaks-windows-only.
    /// </summary>
    public static void SkipGcHandleLeaksOffWindows() =>
        Assert.SkipWhen(!OperatingSystem.IsWindows(),
            "gchandleleaks is a Windows-only SOS command; see issues.md#gchandleleaks-windows-only");

    /// <summary>
    /// <c>enummem</c> (the ICLRDataEnumMemoryRegions test command) is not surfaced by the lldb SOS plugin and
    /// comes back as an unrecognized command there; it runs on the dbgeng and dotnet-dump hosts. See
    /// issues.md#enummem-lldb.
    /// </summary>
    public static void SkipEnumMemOnLldb(Host host) =>
        Assert.SkipWhen(host == Host.Lldb,
            "enummem is not surfaced by the lldb SOS plugin; see issues.md#enummem-lldb");

    /// <summary>
    /// The <c>do</c> alias for <c>dumpobj</c> collides with lldb's built-in <c>do</c> command, so it can't be
    /// dispatched through the lldb SOS host — <c>sos do</c> is reported as an unknown SOS command. The
    /// primary <c>dumpobj</c> spelling works on lldb; only the alias is unavailable. See issues.md#do-alias-lldb.
    /// </summary>
    public static void SkipDoAliasOnLldb(Host host) =>
        Assert.SkipWhen(host == Host.Lldb,
            "the 'do' alias collides with lldb's built-in 'do' command; see issues.md#do-alias-lldb");

    // ---------------------------------------------------------------------------------------------------
    // .NET 11 (preview) baselines. These are *baselined* (recorded as known failures), not yet fixed: the
    // fixes land separately. Each is greppable so re-enabling is a one-line delete once the underlying
    // runtime/SOS work is done. See the matching anchors in issues.md.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// On .NET 11 some SOS maintenance commands (e.g. <c>sosflush</c>/<c>enummem</c>) return E_NOTIMPL
    /// (0x80004001, surfaced as "Unrecognized SOS command") under the cDAC on the dotnet-dump host. See
    /// issues.md#cdac-net11-notimpl.
    /// </summary>
    public static void SkipCDacNet11NotImplemented(TestConfig config) =>
        Assert.SkipWhen(config.CoreVersion == CoreVersion.Net11 && config.Dac == Dac.CDac && config.Host == Host.DotnetDump,
            "some SOS commands return E_NOTIMPL under the cDAC on .NET 11; see issues.md#cdac-net11-notimpl");
}
