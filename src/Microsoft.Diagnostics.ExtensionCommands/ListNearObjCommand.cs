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
            ClrSegment segment = heap.GetSegmentByAddress(objAddress);
            if (segment is null)
            {
                Console.WriteLine($"Failed to find the segment of the managed heap where the object {objAddress:x} resides");
                return;
            }

            // If we have allocation contexts in the target memory range, expand the pointer size column
            // so that we can print the allocation context range.
            MemoryRange[] segAllocContexts = heap.EnumerateAllocationContexts().Where(context => segment.ObjectRange.Contains(context.Start)).ToArray();
            int pointerColumnWidth = segAllocContexts.Length > 0 ? 33 : 16;

            TableOutput output = new(Console, (-"Current:".Length, ""), (pointerColumnWidth, "x16"), (20, ""), (0, ""));

            ClrObject prev = heap.FindPreviousObjectOnSegment(objAddress, carefully: true);
            ClrObject curr = heap.GetObject(objAddress);
            if (prev.Address == 0)
            {
                if (segment.FirstObjectAddress == objAddress)
                {

                    Console.WriteLine($"Object {objAddress:x} is the first object on segment {segment.Address:x}.");
                    localConsistency = VerifyAndPrintObject(output, "Current:", heap, curr) && localConsistency;

                    ulong expectedNextObj = Align(curr + curr.Size, segment);
                    MemoryRange allocContextPlusGap = PrintGap(output, segment, segAllocContexts, new(curr, expectedNextObj));
                    if (allocContextPlusGap.End != 0)
                    {
                        expectedNextObj = allocContextPlusGap.End;
                    }

                    if (!segment.ObjectRange.Contains(expectedNextObj))
                    {
                        isLastObject = true;
                    }
                }
                else
                {
                    // This shouldn't happen, we should always find a previous object since we know objAddress
                    // isn't the first object.  Just in case, print out an error message:
                    Console.WriteLine($"Before: couldn't find any object between {segment.FirstObjectAddress:x} and {objAddress:x}");

                    localConsistency = false;
                }
            }
            else
            {
                if (curr.IsValid)
                {
                    // objAddress directly points at an object, print normal output:
                    localConsistency = VerifyAndPrintObject(output, "Before:", heap, prev) && localConsistency;
                    PrintGap(output, segment, segAllocContexts, new(prev, curr));
                    localConsistency = VerifyAndPrintObject(output, "Current:", heap, curr) && localConsistency;

                    ulong expectedNextObj = Align(curr + curr.Size, segment);
                    MemoryRange allocContextPlusGap = PrintGap(output, segment, segAllocContexts, new(curr, expectedNextObj));
                    if (allocContextPlusGap.End != 0)
                    {
                        expectedNextObj = allocContextPlusGap.End;
                    }

                    if (!segment.ObjectRange.Contains(expectedNextObj))
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
                        PrintGap(output, segment, segAllocContexts, new(prev, curr));
                        VerifyAndPrintObject(output, "Current:", heap, curr);

                        localConsistency = false;
                    }
                    else if (expectedNext > objAddress)
                    {
                        // objAddress was in the middle of an object (or an allocation context).  We simply
                        // won't print "Current:" in this case.
                        localConsistency = VerifyAndPrintObject(output, "Before:", heap, prev) && localConsistency;
                    }
                    else if (expectedNext == 0)
                    {
                        // Couldn't find a next object on the segment, this is the last object.
                        VerifyAndPrintObject(output, "Before:", heap, prev);
                        MemoryRange allocContextPlusGap = PrintGap(output, segment, segAllocContexts, new(prev, curr));
                        if (allocContextPlusGap.Contains(objAddress) || !segment.ObjectRange.Contains(objAddress))
                        {
                            // objAdress is either in the allocationContext, or the sliver of memory between the
                            // end and the next valid address, or we are past the allocated region.  Nothing
                            // to do here.
                            isLastObject = true;
                        }
                        else
                        {
                            // We somehow couldn't walk past prev, and this address should be in allocated bounds
                            // of the segment, print it out and mark consistency false:
                            VerifyAndPrintObject(output, "Current:", heap, curr);
                            localConsistency = false;
                        }
                    }
                    else
                    {
                        // ClrMD somehow returned that there exists an object between the previous object and this
                        // object at the same time.  We'll print the current and previous object here for diagnostics,
                        // but this should never happen

                        VerifyAndPrintObject(output, "Before:", heap, prev);
                        PrintGap(output, segment, segAllocContexts, new(prev, expectedNext));
                        VerifyAndPrintObject(output, "???", heap, expectedNext);
                        PrintGap(output, segment, segAllocContexts, new(expectedNext, curr));
                        VerifyAndPrintObject(output, "Current:", heap, curr);
                    }
                }
            }

            ClrObject next = heap.FindNextObjectOnSegment(objAddress, carefully: true);
            if (!segment.ObjectRange.Contains(objAddress))
            {
                Console.WriteLine($"Object {objAddress:x} is outside of the allocated range ({segment.ObjectRange.Start:x}-{segment.ObjectRange.End:x}) on segment {segment.Address:x}.");
            }
            else if (next.Address == 0)
            {
                if (isLastObject)
                {
                    Console.WriteLine($"No objects at or after {objAddress:x} on segment {segment.Address:x}");
                }
                else
                {
                    Console.WriteLine($"After:  couldn't find any object between {objAddress:x} and {segment.ObjectRange.End:x}");
                    localConsistency = false;
                }
            }
            else
            {
                // VerifyAndPrintObject will handle if next isn't valid
                PrintGap(output, segment, segAllocContexts, new(objAddress, next));
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

        private MemoryRange PrintGap(TableOutput output, ClrSegment segment, MemoryRange[] segAllocContexts, MemoryRange objectDistance)
        {
            // Print information about allocation context gaps between objects
            MemoryRange range = segAllocContexts.FirstOrDefault(ctx => objectDistance.Overlaps(ctx));
            if (range.Start != 0)
            {
                output.WriteRow("Gap:", $"{range.Start:x}-{range.End:x}", FormatSize(range.Length), "GC Allocation Context (expected gap in the heap)");
            }

            // Return the region of memory that does not contain objects.  CLR stores allocation contexts with an ending
            // that's min_object_size away from the next valid object.  We want to display the alloc_context as CLR sees it,
            // but we also need to know the invalid memory range to be sure we don't display a bad error message.
            if (range.End == 0)
            {
                return default;
            }

            uint minObjectSize = (uint)MemoryService.PointerSize * 3;
            return new(range.Start, range.End + Align(minObjectSize, segment));
        }

        private static ulong Align(ulong size, ClrSegment seg)
        {
            ulong AlignConst;
            ulong AlignLargeConst = 7;

            if (IntPtr.Size == 4)
            {
                AlignConst = 3;
            }
            else
            {
                AlignConst = 7;
            }

            if (seg.Kind is GCSegmentKind.Large or GCSegmentKind.Pinned)
            {
                return (size + AlignLargeConst) & ~AlignLargeConst;
            }

            return (size + AlignConst) & ~AlignConst;
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
                output.WriteRow(which, new DmlListNearObj(obj), size, typeName);
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
