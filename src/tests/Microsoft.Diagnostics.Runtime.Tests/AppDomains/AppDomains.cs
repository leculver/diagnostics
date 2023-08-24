// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable CA1050 // Declare types in namespaces
#pragma warning disable CA1823 // Avoid unused private fields
#pragma warning disable IDE0051 // Remove unused private members
#pragma warning disable IDE0052 // Remove unread private members

using System;
using System.IO;
using System.Reflection;
using System.Threading;

internal static class Program
{
    private static readonly Foo s_foo = new();
    private static void Main(string[] _)
    {
        string codebase = Assembly.GetExecutingAssembly().Location;

        if (codebase.StartsWith("file://"))
        {
            codebase = codebase.Substring(8).Replace('/', '\\');
        }

        SharedStaticTest.Value = 2;

        AppDomain domain = AppDomain.CreateDomain("Second AppDomain");
        domain.ExecuteAssembly(Path.Combine(Path.GetDirectoryName(codebase), "NestedException.exe"));

        while (true)
        {
            Thread.Sleep(250);
        }
    }
}
