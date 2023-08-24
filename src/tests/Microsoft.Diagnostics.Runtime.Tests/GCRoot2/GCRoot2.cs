// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable 0162
#pragma warning disable CA1050 // Declare types in namespaces
#pragma warning disable CA1823 // Avoid unused private fields
#pragma warning disable IDE0051 // Remove unused private members
#pragma warning disable IDE0052 // Remove unread private members
#pragma warning disable IDE0059 // Unnecessary assignment of a value

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// GCHandle \
//            DirectTarget -- IndirectTarget
// GCHandle /
internal sealed class GCRootTarget
{
    private static void Main()
    {
        Alloc();
        throw new Exception();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Alloc()
    {
        DirectTarget source = new()
        {
            Item = new IndirectTarget()
        };

        GCHandle.Alloc(source);
        GCHandle.Alloc(source);
    }
}

internal sealed class DirectTarget
{
    public object Item;
}

internal sealed class IndirectTarget
{
}
