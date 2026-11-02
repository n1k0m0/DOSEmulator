// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;

namespace DOSEmulator.Whpx;

internal sealed unsafe partial class WhpxInstructionEmulator
{
    private readonly WhpxNative.RegisterValue[] _fpuContinuationValues=new WhpxNative.RegisterValue[28];
    private readonly byte[] _fpuContinuationExit=new byte[256];
    private bool _fpuContinuationAvailable,_usingFpuContinuation;
    private ulong _fpuContinuationDataPage,_fpuContinuationCodePage;
    private uint _fpuContinuationPhysicalPage;
    /// <summary>Derived count of additional real FSTP instructions completed while the native processor remains stopped.</summary>
    internal long AdditionalFpuStores {get;private set;}
    /// <summary>Pure owner-side availability hint; an ordinary MMIO exit never pays another device-time boundary for this path.</summary>
    internal bool HasFpuContinuation=>_fpuContinuationAvailable;

    /// <summary>A native CPU run invalidates every cached register/translation premise; no continuation state enters a snapshot.</summary>
    internal void ClearFpuContinuation()=>_fpuContinuationAvailable=false;

    /// <summary>Retains only a completed parameter write followed by the exact supported adjacent store form.</summary>
    private void RememberFpuContinuation(WhpxNative.RegisterValue* values,uint* names,WhpxNative.RegisterValue* updated,uint count,
        ReadOnlySpan<byte> code,int length,int width,int operation,uint physical,ulong linear)
    {
        _fpuContinuationAvailable=false;
        if(width!=4||operation!=3||length!=3||code[0]!=0xd9||code[1]!=0x5f||code.Length<6||code[3]!=0xd9||code[4]!=0x5f||
            !_bus.IsImmediateDwordWrite(physical))return;
        for(int i=0;i<28;i++)_fpuContinuationValues[i]=values[i];
        for(int i=0;i<count;i++)
        {
            int slot=names[i] switch{0x10=>8,0x1018=>26,>=0x1010 and <=0x1017=>18+(int)(names[i]-0x1010),_=>-1};
            if(slot<0)throw new InvalidOperationException("Unexpected FSTP continuation register");
            _fpuContinuationValues[slot]=updated[i];
        }
        _fpuContinuationDataPage=linear>>12;_fpuContinuationCodePage=(values[11].Low+values[8].Low)>>12;
        _fpuContinuationPhysicalPage=physical>>12;_fpuContinuationAvailable=true;
    }

    /// <summary>Executes one adjacent FSTP through the same full single-instruction preflight; the owner services time/IRQs before every call.</summary>
    internal bool TryCompleteConsecutiveFpuStore()
    {
        if(!_fpuContinuationAvailable)return false;_fpuContinuationAvailable=false;
        uint* names=stackalloc uint[]{0x10,0x13,0x1018,WhpxNative.InterruptState,WhpxNative.PendingInterruption,WhpxNative.PendingEvent};
        WhpxNative.RegisterValue* current=stackalloc WhpxNative.RegisterValue[6];
        WhpxNative.Check(WhpxNative.WHvGetVirtualProcessorRegisters(_partition,0,names,6,current),"check stopped FSTP continuation boundary");
        var saved=_fpuContinuationValues;
        if(current[0].Low!=saved[8].Low||current[1].Low!=saved[11].Low||current[1].High!=saved[11].High||
            current[2].Low!=saved[26].Low||current[2].High!=saved[26].High||(current[3].Low&3)!=0||
            (current[4].Low&1)!=0||(current[5].Low&1)!=0)return false;
        ulong rip=saved[8].Low,linear=saved[11].Low+rip,limit=saved[11].High&uint.MaxValue;
        if(linear>uint.MaxValue||linear>>12!=_fpuContinuationCodePage||(linear&4095)>4087||rip+8>limit)return false;
        ulong page=ulong.MaxValue,physicalPage=0;
        for(int i=0;i<9;i++)
        {
            if(!TryFpuPageByte(linear+(uint)i,0x14,ref page,ref physicalPage,out uint physical)||!_bus.TryReadRamByte(physical,out byte value))return false;
            _fpuContinuationExit[52+i]=value;
        }
        if(_fpuContinuationExit[52]!=0xd9||_fpuContinuationExit[53]!=0x5f)return false;
        uint offset=unchecked((uint)saved[7].Low+(uint)(sbyte)_fpuContinuationExit[54]);
        if((saved[13].Low+offset)>>12!=_fpuContinuationDataPage)return false;
        _fpuContinuationExit[48]=9;BinaryPrimitives.WriteUInt32LittleEndian(_fpuContinuationExit.AsSpan(68),5);
        _usingFpuContinuation=true;
        try
        {
            if(!TryCompleteProtectedFpuStore(_fpuContinuationExit))return false;
            AdditionalFpuStores++;return true;
        }
        finally{_usingFpuContinuation=false;}
    }
}
