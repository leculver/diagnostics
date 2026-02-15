// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Microsoft.Diagnostics.Tracing.Stacks;

namespace Microsoft.Internal.Common.Utils
{
    /// <summary>
    /// A delegating StackSource wrapper that strips synthetic ".il" and ".ni" suffixes
    /// from module names in frame strings. TraceEvent's TraceLog creates synthetic paths
    /// like "System.Private.CoreLib.il.dll" for ReadyToRun assemblies to track separate
    /// IL/native PDB info. This causes display names to show as "System.Private.CoreLib.il"
    /// instead of "System.Private.CoreLib". This wrapper fixes the display names while
    /// leaving the underlying StackSource data unchanged.
    /// See: https://github.com/dotnet/diagnostics/issues/3102
    /// </summary>
    internal sealed class EventPipeStackSourceFixup : StackSource
    {
        private readonly StackSource _source;

        public EventPipeStackSourceFixup(StackSource source)
        {
            _source = source;
        }

        public override void ForEach(Action<StackSourceSample> callback) => _source.ForEach(callback);

        public override bool SamplesImmutable => _source.SamplesImmutable;

        public override StackSourceCallStackIndex GetCallerIndex(StackSourceCallStackIndex callStackIndex)
            => _source.GetCallerIndex(callStackIndex);

        public override StackSourceFrameIndex GetFrameIndex(StackSourceCallStackIndex callStackIndex)
            => _source.GetFrameIndex(callStackIndex);

        public override string GetFrameName(StackSourceFrameIndex frameIndex, bool verboseName)
        {
            string name = _source.GetFrameName(frameIndex, verboseName);
            return StripILSuffix(name);
        }

        public override int CallStackIndexLimit => _source.CallStackIndexLimit;

        public override int CallFrameIndexLimit => _source.CallFrameIndexLimit;

        public override bool OnlyManagedCodeStacks
        {
            get => _source.OnlyManagedCodeStacks;
            set => _source.OnlyManagedCodeStacks = value;
        }

        public override StackSourceSample GetSampleByIndex(StackSourceSampleIndex sampleIndex)
            => _source.GetSampleByIndex(sampleIndex);

        public override int SampleIndexLimit => _source.SampleIndexLimit;

        public override double SampleTimeRelativeMSecLimit => _source.SampleTimeRelativeMSecLimit;

        /// <summary>
        /// Strips the ".il" or ".ni" suffix from the module portion of a frame name.
        /// Frame names have the form "Module!Method" — the suffix appears in the module part.
        /// For example, "System.Console.il!System.Console.Read()" becomes
        /// "System.Console!System.Console.Read()".
        /// </summary>
        internal static string StripILSuffix(string frameName)
        {
            if (frameName == null)
            {
                return frameName;
            }

            int bangIndex = frameName.IndexOf('!');
            if (bangIndex < 3)
            {
                return frameName;
            }

            // Check if the module name (before '!') ends with ".il" or ".ni"
            ReadOnlySpan<char> module = frameName.AsSpan(0, bangIndex);
            if (module.EndsWith(".il".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                module.EndsWith(".ni".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return string.Concat(frameName.AsSpan(0, bangIndex - 3), frameName.AsSpan(bangIndex));
            }

            return frameName;
        }
    }
}
