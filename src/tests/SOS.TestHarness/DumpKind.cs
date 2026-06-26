// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace SOS.TestHarness;

/// <summary>
/// The kind of dump captured for a target — a matrix axis (flags, like <see cref="Flavor"/>). Selects how
/// much of the process the dump contains, which lets tests validate SOS against reduced dumps.
/// <list type="bullet">
///   <item><see cref="Full"/> — the complete dump SOS/ClrMD normally need (createdump
///   <c>DOTNET_DbgMiniDumpType=4</c> / <c>dotnet-dump collect --type Full</c>).</item>
///   <item><see cref="Mini"/> — the smallest dump SOS can still meaningfully analyze: a heap minidump
///   (createdump type 2 "with heap" / <c>--type Heap</c>). It carries the managed heap and stacks but not
///   every module's full memory, so a handful of memory/native commands degrade — which is exactly what a
///   <see cref="Mini"/> test asserts. (A heap-less mini would make most SOS commands fail, so it isn't
///   modeled here.)</item>
/// </list>
/// </summary>
[Flags]
public enum DumpKind
{
    /// <summary>Complete dump (createdump type 4 / <c>--type Full</c>).</summary>
    Full = 1,

    /// <summary>Heap minidump (createdump type 2 / <c>--type Heap</c>) — smallest dump SOS can analyze.</summary>
    Mini = 2,

    /// <summary>Both dump kinds.</summary>
    AllValid = Full | Mini,
}
