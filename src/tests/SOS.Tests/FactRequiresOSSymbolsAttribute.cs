// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace SOS.Tests;

/// <summary>
/// xUnit v3 <see cref="FactAttribute"/> for tests that drive SOS commands needing full OS symbols —
/// e.g. the <c>!address</c>/<c>!maddress</c> family, which can't tag the native address space without
/// <c>ntdll.pdb</c>. The harness gives these hosts a sealed symbol path that adds the PUBLIC <c>msdl</c>
/// server (never symweb) on top of the local cache; on a dev box with outbound HTTPS the symbols resolve
/// and the test runs, while CI agents (no egress) auto-skip.
/// </summary>
/// <remarks>
/// Skips when (a) the host is not Windows, or (b) <c>msdl</c> isn't reachable. Reachability is probed once
/// per process with a short-timeout HTTPS request and cached, so the typical "no outbound HTTPS in CI"
/// environment skips these tests without any pipeline change — and a workstation with symbol-server access
/// runs them. This deliberately mirrors the symbol policy: OS-symbol tests are additive dev coverage, not
/// a CI gate.
/// </remarks>
public sealed class FactRequiresOSSymbolsAttribute : FactAttribute
{
    public FactRequiresOSSymbolsAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath!, sourceLineNumber)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "Windows-only test: requires dbgeng.dll.";
            return;
        }

        if (!s_msdlReachable.Value)
        {
            Skip = "Requires OS symbols (ntdll.pdb) from the public msdl symbol server, which is not " +
                   "reachable here (e.g. CI with no outbound HTTPS). Runs on a workstation with msdl access.";
        }
    }

    private const string MsdlProbeUrl = "https://msdl.microsoft.com/download/symbols/";

    // Probe msdl once per process; a slow/blocked network must not stall test discovery, hence the short
    // timeout. Any non-exception HTTP response (even 404) proves the host is reachable.
    private static readonly Lazy<bool> s_msdlReachable = new(ProbeMsdl, LazyThreadSafetyMode.ExecutionAndPublication);

    private static bool ProbeMsdl()
    {
        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
            using HttpRequestMessage request = new(HttpMethod.Head, MsdlProbeUrl);
            using HttpResponseMessage response = client.Send(request);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
