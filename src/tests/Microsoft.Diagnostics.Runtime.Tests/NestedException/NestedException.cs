// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;

#pragma warning disable CA1050 // Declare types in namespaces
#pragma warning disable CA1823 // Avoid unused private fields
#pragma warning disable IDE0051 // Remove unused private members
#pragma warning disable IDE0052 // Remove unread private members

internal static class Program
{
    public static void Main(string[] _)
    {
        SharedStaticTest.Value = 42;
        Foo foo = new();
        Outer();    /* seq */
        GC.KeepAlive(foo);
    }

    private static void Outer()
    {
        Middle();    /* seq */
    }

    private static void Middle()
    {
        Inner();    /* seq */
    }

    private static void Inner()
    {
        try
        {
            throw new FileNotFoundException("FNF Message");    /* seq */
        }
        catch (FileNotFoundException e)
        {
            throw new InvalidOperationException("IOE Message", e);    /* seq */
        }
    }
}
