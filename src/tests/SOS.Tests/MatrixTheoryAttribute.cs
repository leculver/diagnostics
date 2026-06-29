// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace SOS.Tests;

/// <summary>
/// A <see cref="TheoryAttribute"/> for the SOS test matrix that skips (rather than fails) a theory whose
/// data set is empty. Every SOS test sources its data from <c>TestConfig.BuildMatrix</c>, which filters
/// out configurations that don't apply to the current platform (for example a cdb-only test on Linux/macOS,
/// or an lldb-only test on Windows). When that filtering removes every row the matrix is legitimately
/// empty for this platform — that should report as "skipped", not a failure. xunit's default is to fail a
/// theory with no data, so <see cref="TheoryAttribute.SkipTestWithoutData"/> is enabled here to make the
/// whole suite green across platforms with no per-test annotation.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class MatrixTheoryAttribute : TheoryAttribute
{
    public MatrixTheoryAttribute(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        SkipTestWithoutData = true;
    }
}
