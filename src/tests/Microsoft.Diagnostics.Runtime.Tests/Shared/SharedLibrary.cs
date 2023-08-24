// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable 0169
#pragma warning disable 0414

#pragma warning disable CA1050 // Declare types in namespaces
#pragma warning disable CA1823 // Avoid unused private fields
#pragma warning disable IDE0051 // Remove unused private members
#pragma warning disable IDE0052 // Remove unread private members

public static class SharedStaticTest
{
    public static int Value;
}

public class Foo
{
    private readonly int i = 42;
    private readonly string s = "string";
    private readonly bool b = true;
    private readonly float f = 4.2f;
    private readonly double d = 8.4;
    private readonly object o = new();
    private readonly Struct st;
    private readonly GenericClass<bool, int, float, string, object> g = new();

    public string FooString = "Foo string";

    public void Bar() { }
    public void Baz() { }
    public int Baz(int i) { return i; }
    public T5 GenericBar<T1, T2, T3, T4, T5>(GenericClass<T1, T2, T3, T4, T5> a) { return a.Invoke(default(T1), default(T2), default(T3), default(T4)); }
}

public readonly struct Struct
{
    private readonly int j;
}

public class GenericClass<T1, T2, T3, T4, T5>
{
    public T5 Invoke(T1 a, T2 b, T3 te, T4 t4) { return default(T5); }
}

internal struct EmptyStruct { }

internal readonly struct NestedEmptyStruct
{
    private readonly EmptyStruct es;
}

public class StructTestClass
{
    private readonly Struct s;
    private readonly NestedEmptyStruct nes;
}
