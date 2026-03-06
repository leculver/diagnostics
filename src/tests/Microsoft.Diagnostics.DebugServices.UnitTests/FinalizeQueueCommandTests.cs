// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Diagnostics.ExtensionCommands;
using Xunit;

namespace Microsoft.Diagnostics.DebugServices.UnitTests
{
    public class FinalizeQueueCommandTests
    {
        [Theory]
        [InlineData("System.WeakReference", true)]
        [InlineData("System.WeakReference<System.Object>", true)]
        [InlineData("System.WeakReference<System.String>", true)]
        [InlineData("System.WeakReference<MyApp.MyClass>", true)]
        [InlineData(null, false)]
        [InlineData("System.Object", false)]
        [InlineData("System.IO.FileStream", false)]
        [InlineData("System.Threading.Thread", false)]
        // Derived types of System.WeakReference ARE finalized, so they must NOT be filtered
        [InlineData("MyApp.MyWeakReference", false)]
        [InlineData("System.WeakReferenceOfT", false)]
        [InlineData("System.WeakReferences", false)]
        // Namespace-qualified variants that should not match
        [InlineData("Other.System.WeakReference", false)]
        [InlineData("WeakReference", false)]
        public void IsWeakReferenceTypeName_CorrectlyIdentifiesTypes(string typeName, bool expected)
        {
            bool result = FinalizeQueueCommand.IsWeakReferenceTypeName(typeName);
            Assert.Equal(expected, result);
        }
    }
}
