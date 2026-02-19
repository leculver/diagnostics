// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#undef _TARGET_AMD64_
#ifndef _TARGET_ARM64_
#define _TARGET_ARM64_
#endif

#undef TARGET_AMD64
#ifndef TARGET_ARM64
#define TARGET_ARM64
#endif

#include "strike.h"
#include "util.h"
#include <dbghelp.h>

#include "disasm.h"

#include "corhdr.h"
#include "cor.h"
#include "dacprivate.h"

namespace ARM64GCDump
{
#undef TARGET_X86
#define LF_GCROOTS
#define LL_INFO1000
#define LOG(x)
#define LOG_PIPTR(pObjRef, gcFlags, hCallBack)
#define DAC_ARG(x)
#include "gcdumpnonx86.cpp"
}

#if !defined(_TARGET_WIN64_)
#error This file only supports SOS targeting ARM64 from a 64-bit debugger
#endif

#if !defined(SOS_TARGET_ARM64)
#error This file should be used to support SOS targeting ARM64 debuggees
#endif


void ARM64Machine::IsReturnAddress(TADDR retAddr, TADDR* whereCalled) const
{
    *whereCalled = 0;

    DWORD previousInstr;
    move_xp(previousInstr, retAddr - sizeof(previousInstr));

    if ((previousInstr & 0xfffffc1f) == 0xd63f0000)
    {
        // BLR <reg>
        *whereCalled = 0xffffffff;

        // Try to resolve the target through a jump stub pattern used by NGen/R2R:
        //   ldr xN, [pc, #offset]   (at retAddr - 8)
        //   blr xN                  (at retAddr - 4)
        unsigned int blrReg = (previousInstr >> 5) & 0x1f;
        DWORD prevPrevInstr;
        if (SUCCEEDED(MOVE(prevPrevInstr, retAddr - 2 * sizeof(DWORD))))
        {
            // LDR Xt, literal: 0101 1000 iiii iiii iiii iiii iiit tttt
            if ((prevPrevInstr & 0xff000000) == 0x58000000 &&
                (prevPrevInstr & 0x1f) == blrReg)
            {
                DWORD imm19 = (prevPrevInstr >> 5) & 0x7ffff;
                // offset = SignExtend(imm19:'00', 64)
                INT64 offset = ((INT64)imm19 << 45) >> 43;
                TADDR ldrPC = retAddr - 2 * sizeof(DWORD);
                TADDR targetAddr;
                if (SUCCEEDED(MOVE(targetAddr, ldrPC + offset)))
                {
                    *whereCalled = targetAddr;
                }
            }
        }
    }
    else if ((previousInstr & 0xfc000000) == 0x94000000)
    {
        // BL <label>
        DWORD imm26 = previousInstr & 0x03ffffff;
        // offset = SignExtend(imm26:'00', 64);
        INT64 offset = ((INT64)imm26 << 38) >> 36;
        *whereCalled = retAddr - 4 + offset;
    }
}

// Return 0 for non-managed call.  Otherwise return MD address.
static TADDR MDForCall (TADDR callee)
{
    JITTypes jitType;
    DWORD_PTR methodDesc;
    DWORD_PTR gcinfoAddr;

    // Check if callee points directly to JIT-compiled managed code.
    IP2MethodDesc (callee, methodDesc, jitType, gcinfoAddr);
    if (methodDesc)
        return methodDesc;

    // Follow a jump stub if present:
    //   ldr xN, [pc, #offset]
    //   br xN
    DWORD instr[2];
    if (SUCCEEDED(MOVE(instr[0], callee)) &&
        (instr[0] & 0xff000000) == 0x58000000 &&
        SUCCEEDED(MOVE(instr[1], callee + 4)) &&
        instr[1] == (DWORD)(0xD61F0000 | ((instr[0] & 0x1f) << 5)))
    {
        DWORD imm19 = (instr[0] >> 5) & 0x7ffff;
        INT64 offset = ((INT64)imm19 << 45) >> 43;
        TADDR target;
        if (SUCCEEDED(MOVE(target, callee + offset)))
        {
            IP2MethodDesc (target, methodDesc, jitType, gcinfoAddr);
            return methodDesc;
        }
    }

    return 0;
}

// Determine if a value is MT/MD/Obj
static void HandleValue(TADDR value)
{
    // A MethodTable?
    if (IsMethodTable(value))
    {
        NameForMT_s (value, g_mdName,mdNameLen);
        ExtOut (" (MT: %S)", g_mdName);
        return;
    }
    
    // A Managed Object?
    TADDR dwMTAddr;
    move_xp (dwMTAddr, value);
    if (IsStringObject(value))
    {
        ExtOut (" (\"");
        StringObjectContent (value, TRUE);
        ExtOut ("\")");
        return;
    }
    else if (IsMethodTable(dwMTAddr))
    {
        NameForMT_s (dwMTAddr, g_mdName,mdNameLen);
        ExtOut (" (Object: %S)", g_mdName);
        return;
    }
    
    // A MethodDesc?
    if (IsMethodDesc(value))
    {        
        NameForMD_s (value, g_mdName,mdNameLen);
        ExtOut (" (MD: %S)", g_mdName);
        return;
    }

    // A JitHelper?
    const char* name = HelperFuncName(value);
    if (name) {
        ExtOut (" (JitHelp: %s)", name);
        return;
    }

    // A call to managed code?
    TADDR methodDesc = MDForCall(value);
    if (methodDesc)
    {
        NameForMD_s (methodDesc, g_mdName,mdNameLen);
        ExtOut (" (code for MD: %S)", g_mdName);
        return;
    }
    
    // Random symbol.
    char Symbol[1024];
    if (SUCCEEDED(g_ExtSymbols->GetNameByOffset(TO_CDADDR(value), Symbol, 1024,
                                                NULL, NULL)))
    {
        if (Symbol[0] != '\0')
        {
            ExtOut (" (%s)", Symbol);
            return;
        }
    }
    
}

/**********************************************************************\
* Routine Description:                                                 *
*                                                                      *
*    Unassembly a managed code.  Translating managed object,           *  
*    call.                                                             *
*                                                                      *
\**********************************************************************/
void ARM64Machine::Unassembly (
    TADDR PCBegin, 
    TADDR PCEnd, 
    TADDR PCAskedFor, 
    TADDR GCStressCodeCopy, 
    GCEncodingInfo *pGCEncodingInfo, 
    SOSEHInfo *pEHInfo,
    BOOL bSuppressLines,
    BOOL bDisplayOffsets,
    std::function<void(ULONG*, UINT*, BYTE*)> displayIL) const
{
    ULONG_PTR PC = PCBegin;
    char line[1024];
    ULONG lineNum;
    ULONG curLine = -1;
    WCHAR fileName[MAX_LONGPATH];
    char *ptr;
    INT_PTR accumulatedConstant = 0;
    BOOL loBitsSet = FALSE;
    BOOL hiBitsSet = FALSE;
    char *szConstant = NULL;
    ULONG ilPosition = 0;
    UINT ilIndentCount = 0;
    TADDR adrpPageAddr = 0;
    BOOL adrpSet = FALSE;


    while(PC < PCEnd)
    {
        ULONG_PTR currentPC = PC;
        DisasmAndClean (PC, line, ARRAY_SIZE(line));

        // This is the closing of the previous run. 
        // Check the next instruction. if it's not a the last movk, handle the accumulated value
        // else simply print a new line.
        if (loBitsSet && hiBitsSet)
        {
            ptr = line;
            // Advance to the instruction encoding
            NextTerm(ptr);
            // Advance to the opcode
            NextTerm(ptr);
            // if it's not movk, handle the accumulated value
            // otherwise simply print the new line. The constant in this expression will be 
            // accumulated below.
            if (strncmp(ptr, "movk ", 5))
            {
                HandleValue(accumulatedConstant);
                accumulatedConstant = 0;
            }
            ExtOut ("\n");
        }
        else if (currentPC != PCBegin)
        {
            ExtOut ("\n");
        }
        
        // This is the new instruction

        if (IsInterrupt())
            return;
        //
        // Print out line numbers if needed
        //
        if (!bSuppressLines && 
            SUCCEEDED(GetLineByOffset(TO_CDADDR(currentPC), &lineNum, fileName, MAX_LONGPATH)))
        {
            if (lineNum != curLine)
            {
                curLine = lineNum;
                ExtOut("\n%S @ %d:\n", fileName, lineNum);
            }
        }
        displayIL(&ilPosition, &ilIndentCount, (BYTE*)PC);

        //
        // Print out any GC information corresponding to the current instruction offset.
        //
        if (pGCEncodingInfo)
        {
            SIZE_T curOffset = (currentPC - PCBegin) + pGCEncodingInfo->hotSizeToAdd;
            pGCEncodingInfo->DumpGCInfoThrough(curOffset);
        }

        //
        // Print out any EH info corresponding to the current offset
        //
        if (pEHInfo)
        {
            pEHInfo->FormatForDisassembly(currentPC - PCBegin);
        }
        
        if (currentPC == PCAskedFor)
        {
            ExtOut (">>> ");
        }

        //
        // Print offsets, in addition to actual address.
        //
        if (bDisplayOffsets)
        {
            ExtOut("%04x ", currentPC - PCBegin);
        }

        // look at the disassembled bytes
        ptr = line;
        NextTerm (ptr);

        //
        // If there is gcstress info for this method, and this is a 'hlt'
        // instruction, then gcstress probably put the 'hlt' there.  Look
        // up the original instruction and print it instead.
        //        
        

        if (   GCStressCodeCopy
            && (   !strncmp (ptr, "badc0de0", 8)
                || !strncmp (ptr, "badc0de1", 8)
                || !strncmp (ptr, "badc0de2", 8)
                ))
        {
            ULONG_PTR InstrAddr = currentPC;

            //
            // Compute address into saved copy of the code, and
            // disassemble the original instruction
            //
            
            ULONG_PTR OrigInstrAddr = GCStressCodeCopy + (InstrAddr - PCBegin);
            ULONG_PTR OrigPC = OrigInstrAddr;

            DisasmAndClean(OrigPC, line, ARRAY_SIZE(line));

            //
            // Increment the real PC based on the size of the unmodifed
            // instruction
            //

            PC = InstrAddr + (OrigPC - OrigInstrAddr);

            //
            // Print out real code address in place of the copy address
            //

            ExtOut("%08x`%08x ", (ULONG)(InstrAddr >> 32), (ULONG)InstrAddr);

            ptr = line;
            NextTerm (ptr);

            //
            // Print out everything after the code address, and skip the
            // instruction bytes
            //

            ExtOut(ptr);

            //
            // Add an indicator that this address has not executed yet
            //

            ExtOut(" (gcstress)");
        }
        else
        {
            ExtOut (line);
        }

        // Now advance to the opcode
        NextTerm (ptr);

        if (!strncmp(ptr, "mov ", 4))
        {
            if ((szConstant = strchr(ptr, '#')) != NULL)
            {
                GetValueFromExpr(szConstant, accumulatedConstant);
                loBitsSet = TRUE;
            }
        }
        else if (!strncmp(ptr, "movk ", 5))
        {
            char *szShiftAmount = NULL;
            INT_PTR shiftAmount = 0;
            INT_PTR constant = 0;
            if (((szShiftAmount = strrchr(ptr, '#')) != NULL) &&
                ((szConstant = strchr(ptr, '#')) != NULL) &&
                (szShiftAmount != szConstant) &&
                (accumulatedConstant > 0)) // Misses when movk is succeeding mov reg, #0x0, which I don't think makes any sense 
            {
                GetValueFromExpr(szShiftAmount, shiftAmount);
                GetValueFromExpr(szConstant, constant);
                accumulatedConstant += (constant<<shiftAmount);
                hiBitsSet = TRUE;
            }
        }
        else 
        {
            accumulatedConstant = 0;
            loBitsSet = hiBitsSet = FALSE;
            if ((szConstant = strchr(ptr, '=')) != NULL)
            {
                // Some instruction fetched a PC-relative constant which the disassembler nicely decoded for
                // us using the ARM convention =<constant>. Retrieve this value and see if it's interesting.
                INT_PTR value;
                GetValueFromExpr(szConstant, value);
                HandleValue(value);
                adrpSet = FALSE;
            }
            else if (!strncmp(ptr, "adrp ", 5))
            {
                // Track the ADRP page address for resolution with a subsequent LDR/ADD.
                // adrp sets bits [63:12] of the target address; the next instruction adds the page offset.
                char *szAddr = strchr(ptr, '#');
                if (szAddr != NULL)
                {
                    INT_PTR page;
                    GetValueFromExpr(szAddr, page);
                    adrpPageAddr = (TADDR)page;
                    adrpSet = TRUE;
                }
                else
                {
                    adrpSet = FALSE;
                }
            }
            else if (adrpSet && (!strncmp(ptr, "ldr ", 4) || !strncmp(ptr, "add ", 4)))
            {
                // Resolve adrp/ldr or adrp/add pair to get the final address.
                char *szOffset = strchr(ptr, '#');
                if (szOffset != NULL)
                {
                    INT_PTR offset;
                    GetValueFromExpr(szOffset, offset);
                    HandleValue(adrpPageAddr + (TADDR)offset);
                }
                adrpSet = FALSE;
            }
            else
            {
                adrpSet = FALSE;
            }
        }
                
    }
    ExtOut ("\n");

    //
    // Print out any "end" GC info
    //
    if (pGCEncodingInfo)
    {
        pGCEncodingInfo->DumpGCInfoThrough(PC - PCBegin);
    }

    //
    // Print out any "end" EH info (where the end address is the byte immediately following the last instruction)
    //
    if (pEHInfo)
    {
        pEHInfo->FormatForDisassembly(PC - PCBegin);
    }
}


// @ARMTODO: Figure out how to extract this information under CoreARM
BOOL ARM64Machine::GetExceptionContext (TADDR stack, TADDR PC, TADDR *cxrAddr, CROSS_PLATFORM_CONTEXT * cxr,
                          TADDR * exrAddr, PEXCEPTION_RECORD exr) const
{
    _ASSERTE("ARM64:NYI");
    return FALSE;
}

///
/// Dump ARM GCInfo table
///
void ARM64Machine::DumpGCInfo(GCInfoToken gcInfoToken, unsigned methodSize, printfFtn gcPrintf, bool encBytes, bool bPrintHeader) const
{
    if (bPrintHeader)
    {
        ExtOut("Pointer table:\n");
    }

    ARM64GCDump::GCDump gcDump(gcInfoToken.Version, encBytes, 5, true);
    gcDump.gcPrintf = gcPrintf;

    gcDump.DumpGCTable(dac_cast<PTR_BYTE>(gcInfoToken.Info), methodSize, 0);
}

