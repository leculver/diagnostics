// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Globalization;
using System.Linq;
using Microsoft.Diagnostics.DebugServices;
using Microsoft.Diagnostics.Runtime;
using static Microsoft.Diagnostics.ExtensionCommands.TableOutput;

namespace Microsoft.Diagnostics.ExtensionCommands
{
    [Command(Name = "listnearobj", Help = "Displays the object preceding and succeeding the specified address.")]
    public class ListNearObjCommand : CommandBase
    {
        [ServiceImport]
        public ClrRuntime Runtime { get; set; }

        [ServiceImport]
        public IMemoryService MemoryService { get; set; }

        [Argument(Help = "The address on the GC heap to list near objects")]
        public string Address { get; set; }

        public override void Invoke()
        {
            if (!ulong.TryParse(Address, NumberStyles.HexNumber, null, out ulong objAddress))
            {
                throw new ArgumentException($"Could not parse address: {Address}");
            }

            // Align objAddress
            objAddress &= ~((ulong)MemoryService.PointerSize - 1);

            bool localConsistency = true;
            bool isLastObject = false;

            ClrHeap heap = Runtime.Heap;
            ClrSegment seg = heap.GetSegmentByAddress(objAddress);
            if (seg is null)
            {
                Console.WriteLine($"Failed to find the segment of the managed heap where the object {objAddress:x} resides");
                return;
            }

            // If we have allocation contexts in the target memory range, expand the pointer size column
            // so that we can print the allocation context range.
            MemoryRange[] segAllocContexts = heap.EnumerateAllocationContexts().Where(context => seg.ObjectRange.Contains(context.Start)).ToArray();
            int pointerColumnWidth = segAllocContexts.Length > 0 ? 33 : 16;

            TableOutput output = new(Console, (-"Current:".Length, ""), (pointerColumnWidth, "x16"), (20, ""), (0, ""));

            if (seg.FirstObjectAddress == objAddress)
            {
                Console.WriteLine($"Object {objAddress:x} is the first object on segment {seg.Address:x}.");
            }
            else
            {
                ClrObject prev = heap.FindPreviousObjectOnSegment(objAddress, carefully: true);
                if (prev.Address == 0)
                {
                    // This shouldn't happen, we should always find a previous object since we know objAddress
                    // isn't the first object.  Just in case, print out an error message:
                    Console.WriteLine($"Before: couldn't find any object between {seg.FirstObjectAddress:x} and {objAddress:x}");

                    localConsistency = false;
                }
                else
                {
                    ClrObject curr = heap.GetObject(objAddress);
                    if (curr.IsValid)
                    {
                        // objAddress directly points at an object, print normal output:
                        localConsistency = VerifyAndPrintObject(output, "Before:", heap, prev) && localConsistency;
                        localConsistency = VerifyAndPrintObject(output, "Current:", heap, curr) && localConsistency;

                        if (!seg.CommittedMemory.Contains(curr + curr.Size))
                        {
                            isLastObject = true;
                        }
                    }
                    else
                    {
                        // Find the object after prev and see if we have a corrupted object, or of objAddress simply
                        // was simply in the middle of an object or allocation context:

                        ClrObject expectedNext = heap.FindNextObjectOnSegment(prev);
                        if (expectedNext == objAddress)
                        {
                            // Ok, the current address isn't a valid object, but it SHOULD be.  Normal output
                            // will print the error:
                            VerifyAndPrintObject(output, "Before:", heap, prev);
                            PrintGap(output, segAllocContexts, curr);
                            VerifyAndPrintObject(output, "Current:", heap, curr);

                            localConsistency = false;
                        }
                        else if (expectedNext > objAddress)
                        {
                            // objAddress was in the middle of an object (or an allocation context).  We simply
                            // won't print "Current:" in this case.
                            localConsistency = VerifyAndPrintObject(output, "Before:", heap, prev) && localConsistency;
                        }
                        else
                        {
                            // This should never happen.  This means ClrMD is returning inconsistent results.
                            // We'll still handle the case here for defensive coding reasons.

                            VerifyAndPrintObject(output, "Before:", heap, prev);
                            PrintGap(output, segAllocContexts, curr);
                            VerifyAndPrintObject(output, "Current:", heap, curr);

                            localConsistency = false;
                        }
                    }
                }
            }

            ClrObject next = heap.FindNextObjectOnSegment(objAddress, carefully: true);
            if (next.Address == 0)
            {
                if (isLastObject)
                {
                    Console.WriteLine($"Object {objAddress:x} is the last object on segment {seg.Address:x}");
                }
                else
                {
                    Console.WriteLine($"After:  couldn't find any object between {objAddress:x} and {seg.ObjectRange.End:x}");
                    localConsistency = false;
                }
            }
            else
            {
                // VerifyAndPrintObject will handle if next isn't valid
                PrintGap(output, segAllocContexts, next);
                localConsistency = VerifyAndPrintObject(output, "After:", heap, next) && localConsistency;
            }

            if (localConsistency)
            {
                Console.WriteLine("Heap local consistency confirmed.");
            }
            else
            {
                Console.WriteLine("Heap local consistency not confirmed.");
            }
        }

        private static void PrintGap(TableOutput output, MemoryRange[] segAllocContexts, ClrObject curr)
        {
            // Print information about allocation context gaps between objects
            MemoryRange range = segAllocContexts.FirstOrDefault(s => s.End == curr);
            if (range.End == curr)
            {
                output.WriteRow("Gap:", $"{range.Start:x}-{range.End:x}", FormatSize(range.Length), "GC Allocation Context (expected gap in the heap)");
            }
        }

        private bool VerifyAndPrintObject(TableOutput output, string which, ClrHeap heap, ClrObject obj)
        {
            bool isObjectValid = !heap.IsObjectCorrupted(obj, out ObjectCorruption corruption) && obj.IsValid;

            // Here, isCorrupted may still be true, but it might not interfere with getting the type of the object.
            // Since we know the information, we will print that out.
            string typeName = obj.Type?.Name ?? GetErrorTypeName(obj);

            // ClrObject.Size is not available if IsValid returns false
            string size = FormatSize(obj.IsValid ? obj.Size : 0);
            if (corruption is null)
            {
                output.WriteRow(which, new DmlDumpObj(obj), size, typeName);
            }
            else
            {
                output.WriteRow(which, new DmlDumpObj(obj), size, typeName, $"Error Detected: {VerifyHeapCommand.GetObjectCorruptionMessage(MemoryService, heap, corruption)}");
            }

            return isObjectValid;
        }

        private static string FormatSize(ulong size) => size > 0 ? $"{size:n0} (0x{size:x})" : "";

        private string GetErrorTypeName(ClrObject obj)
        {
            if (!MemoryService.ReadPointer(obj.Address, out ulong mt))
            {
                return $"<error reading mt at: {obj.Address:x}>";
            }
            else
            {
                return $"<error reading type name...mt:{mt:x}>";
            }
        }
    }
}
