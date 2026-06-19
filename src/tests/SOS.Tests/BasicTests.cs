// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace SOS.Tests
{
    /// <summary>
    /// Sanity test that proves the modern (xunit v3) SOS test project builds and runs. Real SOS test
    /// coverage and the test harness are ported in follow-up changes.
    /// </summary>
    public class BasicTests
    {
        [Fact]
        public void Sanity()
        {
            Assert.True(true);
        }
    }
}
