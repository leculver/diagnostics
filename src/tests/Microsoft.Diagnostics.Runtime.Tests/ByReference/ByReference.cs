// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Threading;

#pragma warning disable CA1050 // Declare types in namespaces
#pragma warning disable CA1823 // Avoid unused private fields
#pragma warning disable IDE0051 // Remove unused private members
#pragma warning disable IDE0052 // Remove unread private members
#pragma warning disable IDE0059 // Unnecessary assignment of a value

internal static class Program
{
    private static readonly Barrier B = new(4 + 1);

    private static object O = new();
    private static IntPtr I;

    private static void Main()
    {
        new Thread(HeapReferenceTypeOuter).Start();
        new Thread(HeapValueTypeOuter).Start();
        new Thread(StackReferenceTypeOuter).Start();
        new Thread(StackValueTypeOuter).Start();

        B.SignalAndWait();
        Throw();
    }

    private static void HeapReferenceTypeOuter()
    {
        HeapReferenceType(ref O);
    }

    private static void HeapReferenceType(ref object _)
    {
        SignalAndSleep();
    }

    private static void HeapValueTypeOuter()
    {
        HeapValueType(ref I);
    }

    private static void HeapValueType(ref IntPtr _)
    {
        SignalAndSleep();
    }

    private static void StackReferenceTypeOuter()
    {
        object o = new();
        StackReferenceType(ref o);
    }

    private static void StackReferenceType(ref object _)
    {
        SignalAndSleep();
    }

    private static void StackValueTypeOuter()
    {
        IntPtr i = IntPtr.Zero;
        StackValueType(ref i);
    }

    private static void StackValueType(ref IntPtr _)
    {
        SignalAndSleep();
    }

    private static void SignalAndSleep()
    {
        B.SignalAndWait();
        Thread.Sleep(int.MaxValue);
    }

    private static void Throw()
    {
        throw new Exception();
    }
}
