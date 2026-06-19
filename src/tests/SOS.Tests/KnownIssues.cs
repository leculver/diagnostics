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
    /// The cdb (dbgeng) host cannot locate the DAC (<c>mscordaccore.dll</c>) for a self-contained
    /// single-file dump in a hermetic (no symbol-server) test environment: the runtime is bundled inside
    /// the exe, and without a symbol server to download the matching DAC, dbgeng reports "Failed to load
    /// data access module / No CLR runtime found". The dotnet-dump host resolves the DAC itself, so
    /// single-file is still fully covered there. See issues.md#singlefile-cdb-dac.
    /// </summary>
    public static void SkipSingleFileUnderCdb(Host host, Flavor flavor) =>
        Assert.SkipWhen(host == Host.Cdb && flavor == Flavor.SingleFile,
            "cdb cannot locate the DAC for single-file dumps hermetically; see issues.md#singlefile-cdb-dac");

    /// <summary>
    /// Driving the Scenarios debuggee live through the generation-promotion markers (a <c>bpmd</c> on
    /// each of AtGen0/1/2 with a blocking <c>GC.Collect(2)</c> between them) crashes the debuggee with an
    /// internal CLR error under live dbgeng. The dump path exercises the same generation progression, so
    /// gcwhere's "object moves across generations" check runs there. See issues.md#gcwhere-live-gc.
    /// </summary>
    public static void SkipLiveGenPromotion(Liveness liveness) =>
        Assert.SkipWhen(liveness == Liveness.Live,
            "live gen-promotion bpmd + GC.Collect(2) crashes the debuggee under dbgeng; see issues.md#gcwhere-live-gc");
}
