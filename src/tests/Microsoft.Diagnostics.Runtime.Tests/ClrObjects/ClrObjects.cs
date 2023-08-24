// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable 0162
#pragma warning disable CA1050 // Declare types in namespaces
#pragma warning disable CA1823 // Avoid unused private fields
#pragma warning disable IDE0051 // Remove unused private members
#pragma warning disable IDE0052 // Remove unread private members
#pragma warning disable IDE0059 // Unnecessary assignment of a value

using System;

public class Program
{
    public static void Main(string[] args)
    {
        PrimitiveTypeCarrier primitiveObj = new();

        throw new Exception();

        GC.KeepAlive(primitiveObj);
    }
}

public class PrimitiveTypeCarrier
{
    public bool TrueBool = true;

    public long OneLargerMaxInt = ((long)int.MaxValue + 1);

    public DateTime Birthday = new(1992, 1, 24);

    public SamplePointerType SamplePointer = new();

    public EnumType SomeEnum = EnumType.PickedValue;

    public string HelloWorldString = "Hello World";

    public Guid SampleGuid = new("{EB06CEC0-5E2D-4DC4-875B-01ADCC577D13}");
}

public class SamplePointerType
{ }

public enum EnumType { Zero, One, Two, PickedValue }
