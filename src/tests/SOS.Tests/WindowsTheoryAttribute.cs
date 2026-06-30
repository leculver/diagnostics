// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace SOS.Tests;

/// <summary>
/// A <see cref="TheoryAttribute"/> for an SOS test whose matrix is populated only on Windows — for example
/// a cdb-only matrix (<c>!clru</c>, <c>!dumpstack</c>/<c>!eestack</c>), which <c>TestConfig.IsValid</c>
/// gates to the Windows-only cdb host. On Linux/macOS that matrix is legitimately empty, so the OS gate
/// here sets <see cref="FactAttribute.Skip"/> and the theory is skipped <b>before</b> xunit evaluates the
/// (empty) data set. This lets the cross-platform <see cref="SosTheoryAttribute"/> keep xunit's default
/// "empty data is a failure" behaviour, so an unexpectedly empty matrix on a supported platform surfaces as
/// a real failure rather than a silent skip.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute(
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "Only runs on Windows (the cdb-hosted matrix is empty on this platform).";
        }
    }
}
