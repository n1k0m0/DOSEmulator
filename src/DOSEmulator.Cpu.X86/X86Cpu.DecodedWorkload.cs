// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;
public sealed partial class X86Cpu
{
    /// <summary>Identifies actual segment-selector pushes without changing any hidden cache.</summary>
    private static bool IsDecodedSegmentPush(int opcode)=>opcode is 0x06 or 0x0e or 0x16 or 0x1e or 0xfa0 or 0xfa8;
    /// <summary>Retains the scalar eager group-5/0F segment-push checkpoint, even for otherwise ordinary RAM.</summary>
    private static bool RequiresDecodedControlCheckpoint(in DecodedElement element)=>
        element.Kind==DecodedIntegerKind.NearIndirect || element.Kind==DecodedIntegerKind.Stack && element.Operation is 0xff or 0xfa0 or 0xfa8;
    /// <summary>Proves the current indirect target and source access without speculative MMIO, TLB fills, mutation or faults.</summary>
    private bool AdmitDecodedNear(in DecodedElement element,ReadOnlySpan<byte> wholeCode,out IntegerMemoryPlan memory,out uint target)
    {
        memory=default;target=0;
        if(element.Source<0)
        {if(!AdmitDecodedMemory(element,wholeCode,out memory))return false;target=ReadIntegerMemory(memory);}
        else target=ReadRegister(element.Source,element.Width);
        if(element.Width==2)target&=65535;
        return WithinSegment(GetSegmentCache(Segment.Cs),target,1);
    }
    /// <summary>Performs the original fetch/source/optional-push sequence before committing an already validated near target.</summary>
    private void ExecuteDecodedNear(in DecodedElement element,in IntegerMemoryPlan memory,in DecodedStackPlan stack,uint target,int codeSlot)
    {
        TouchIntegerMemoryTranslation(codeSlot,element.CodeTouches);_instructionBytes=element.Length;
        uint returnAddress=unchecked(Eip+(uint)element.Length);Eip=returnAddress;
        if(element.Source<0)TouchIntegerMemoryTranslation(memory.DataSlot);
        if(element.Operation==0xff02)
        {TouchIntegerMemoryTranslation(stack.DataSlot);WriteDecodedStack(in stack,0,returnAddress);SetStackPointer(stack.NextPointer);}
        Eip=target;
    }
    /// <summary>Matches the original single-operand MUL/IMUL result widths, register aliases and CF/OF policy.</summary>
    private void ExecuteDecodedWideMultiply(in DecodedElement element)
    {
        int width=element.Width;uint value=ReadRegister(element.Source,width);ulong product;bool overflow;
        if(element.Operation==4)
        {product=(ulong)ReadRegister(0,width)*value;overflow=(product>>(width*8))!=0;}
        else
        {long signedProduct=Signed(ReadRegister(0,width),width)*Signed(value,width);product=unchecked((ulong)signedProduct);overflow=signedProduct!=Signed((uint)product,width);}
        if(width==1)WriteRegister(0,2,(uint)product);
        else {WriteRegister(0,width,(uint)product);WriteRegister(2,width,(uint)(product>>(width*8)));}
        SetCarryOverflow(overflow);
    }
}
