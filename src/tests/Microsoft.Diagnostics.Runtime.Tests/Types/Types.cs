// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable CA1050 // Declare types in namespaces
#pragma warning disable CA1823 // Avoid unused private fields
#pragma warning disable IDE0051 // Remove unused private members
#pragma warning disable IDE0052 // Remove unread private members
#pragma warning disable IDE0059 // Unnecessary assignment of a value

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

internal sealed class Types
{
    private static readonly object s_one = new();
    private static readonly object s_two = new();
    private static readonly object s_three = new();

    private static readonly object[] s_array = new object[] { s_one, s_two, s_three };
    private static readonly object[,] s_2dArray = new object[0, 0];
    private static readonly object[,,,,] s_5dArray = new object[2, 4, 6, 8, 10];

    private static readonly int[] s_szIntArray = new int[2];
    private static readonly Array s_mdIntArray = Array.CreateInstance(typeof(int), new[] { 2 }, new[] { 1 });
    private static readonly int[,] s_2dIntArray = new int[2, 4];

    private static readonly object[] s_szObjArray = new object[2];
    private static readonly Array s_mdObjArray = Array.CreateInstance(typeof(object), new[] { 2 }, new[] { 1 });
    private static readonly object[,] s_2dObjArray = new object[2, 4];

    private static readonly Foo s_foo = new();
    private static readonly List<int> s_list = new();

    private static readonly object s_i = 42;

    private delegate void TestDelegate1();

    private static event TestDelegate1 TestEvent;

    private delegate void TestDelegate2();

    private static readonly TestDelegate2 TestDelegate = new(Inner);


    public static FileAccess s_enum = FileAccess.Read;

    private static async Task Async() => await Task.Delay(1000).ConfigureAwait(false);

    static Types()
    {
        s_szIntArray[1] = 42;
        s_mdIntArray.SetValue(42, 2);
        s_2dIntArray[1, 2] = 42;

        s_szObjArray[1] = s_szObjArray;
        s_mdObjArray.SetValue(s_mdObjArray, 2);
        s_2dObjArray[1, 2] = s_2dObjArray;

        TestEvent += Inner;
        TestEvent += new Types().InstanceMethod;
    }

    public static void Main()
    {
        new StructTestClass(); // Ensure type is constructed
        Foo f = new();
        Foo[] foos = new Foo[] { f };
        Task task = Async();

        Inner();

        GC.KeepAlive(foos);
    }

    private static void Inner()
    {
        throw new Exception();
    }

    private void InstanceMethod()
    {
        TestEvent.Invoke();
    }
}
