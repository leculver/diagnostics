// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable 0162
#pragma warning disable CA1050 // Declare types in namespaces
#pragma warning disable CA1823 // Avoid unused private fields
#pragma warning disable IDE0051 // Remove unused private members
#pragma warning disable IDE0052 // Remove unread private members
#pragma warning disable IDE0059 // Unnecessary assignment of a value
#pragma warning disable CA1416 // Validate platform compatibility

using System;
using System.Diagnostics;
using System.Threading;
using System.Runtime.InteropServices;

internal static class GCHandles
{
    public static void Main()
    {
        GCHandle.Alloc("normal", GCHandleType.Normal);

        GCHandle.Alloc("pinned", GCHandleType.Pinned);

        string weak = "weak";
        GCHandle.Alloc(weak, GCHandleType.Weak);

        NativeOverlapped nativeOverlapped;

        unsafe
        {
            nativeOverlapped = *new Overlapped().UnsafePack(IOCallback, "state");
        }

        string weakLong = "weakLong";
        GCHandle.Alloc(weak, GCHandleType.WeakTrackResurrection);

        throw new Exception();

        GC.KeepAlive(nativeOverlapped);
        GC.KeepAlive(weak);
        GC.KeepAlive(weakLong);
    }

    private static unsafe void IOCallback(uint errorCode, uint numBytes, NativeOverlapped* pOVERLAP)
    {
    }
}
