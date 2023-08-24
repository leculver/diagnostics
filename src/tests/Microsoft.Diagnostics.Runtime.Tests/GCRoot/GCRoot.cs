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

//                                              object
//                                            /
// SingleRef -- object[] -- DoubleRef -- TripleRef -- SingleRef -- TargetType
//                              \           / \           /
//                                SingleRef     SingleRef
internal static class GCRootTarget
{
    private static object TheRoot;
    private static readonly ConditionalWeakTable<SingleRef, TargetType> _dependent = new();

    public static void Main(string[] _)
    {
        TargetType target = new();
        SingleRef s = new();
        DoubleRef d = new();
        TripleRef t = new();

        TheRoot = s;

        object[] arr = new object[42];
        s.Item1 = arr;
        arr[27] = d;

        // parallel path.
        d.Item1 = new SingleRef() { Item1 = t };
        d.Item2 = t;

        s = new SingleRef();

        t.Item1 = new SingleRef() { Item1 = s };
        t.Item2 = s;
        t.Item3 = new object(); // dead path


        _dependent.Add(s, target);
        //s.Item1 = target;
        throw new Exception();
        GC.KeepAlive(target);
    }
}

internal sealed class SingleRef
{
    public object Item1;
}

internal sealed class DoubleRef
{
    public object Item1;
    public object Item2;
}

internal sealed class TripleRef
{
    public object Item1;
    public object Item2;
    public object Item3;
}

internal sealed class TargetType
{
}
