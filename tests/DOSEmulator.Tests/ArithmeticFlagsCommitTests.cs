// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Numerics;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

/// <summary>Independent unbounded-integer flag oracle for every ordinary ALU operation and operand width.</summary>
internal static class ArithmeticFlagsCommitTests
{
    /// <summary>Requires the mathematical result, every flag or the actual scalar retirement contract.</summary>
    private static void Need(bool value,string why){if(!value)throw new Exception(why);}
    /// <summary>Runs real accumulator-immediate instructions against BigInteger signs, nibble carries and explicit parity.</summary>
    private static void Case(X86CpuModel model,int width,int operation)
    {
        var memory=new MemoryBus(1024*1024);var clock=new DeviceScheduler();
        var cpu=new X86Cpu(memory,new(),model,model==X86CpuModel.IntelP54cB5Subset?clock:null,model==X86CpuModel.IntelP54cB5Subset?100000000u:0);
        cpu.SetSegment(Segment.Cs,0);uint entry=0x2000;int prefix=width==4?1:0;
        if(prefix!=0)memory.WriteByte(entry,0x66);memory.WriteByte(entry+(uint)prefix,(byte)(operation*8+(width==1?4:5)));
        int bits=width*8;uint mask=width==1?255u:width==2?65535u:uint.MaxValue,sign=1u<<(bits-1);
        uint[] edges=[0,1,2,mask-1,mask,sign-1,sign,sign+1];var vectors=new List<(uint A,uint B)>();
        foreach(uint a in edges)foreach(uint b in edges)vectors.Add((a,b));uint seed=0x761ad52b;
        for(int i=0;i<64;i++){seed=unchecked(seed*1664525+1013904223);uint a=seed;seed=unchecked(seed*1664525+1013904223);vectors.Add((a&mask,seed&mask));}
        foreach(var (a,b) in vectors)foreach(uint carry in new uint[]{0,1})
        {
            uint initial=(0x602u|0x8d4u|carry),baseAx=0xe381b7a9u,accumulator=(baseAx&~mask)|a;
            cpu.Eip=entry;cpu.Eflags=initial;cpu.SetRegister(Register.Ax,accumulator);
            for(int i=0;i<width;i++)memory.WriteByte(entry+(uint)(prefix+1+i),(byte)(b>>(i*8)));
            BigInteger x=a,y=b,modulus=BigInteger.One<<bits;
            BigInteger sx=(a&sign)!=0?x-modulus:x,sy=(b&sign)!=0?y-modulus:y,exact,signedExact;
            uint inputCarry=operation is 2 or 3?carry:0;bool cf=false,of=false,af=false;
            if(operation is 0 or 2)
            {exact=x+y+inputCarry;signedExact=sx+sy+inputCarry;cf=exact>mask;of=signedExact < -modulus/2||signedExact>=modulus/2;af=(a&15)+(b&15)+inputCarry>15;}
            else if(operation is 3 or 5 or 7)
            {exact=x-y-inputCarry;signedExact=sx-sy-inputCarry;cf=exact<0;of=signedExact < -modulus/2||signedExact>=modulus/2;af=(a&15)<(b&15)+inputCarry;}
            else exact=operation==1?x|y:operation==4?x&y:x^y;
            uint result=(uint)(exact&mask),flags=(initial&~0x8d5u)|2;int parity=0;
            for(int bit=0;bit<8;bit++)parity^=(int)(result>>bit)&1;
            if(cf)flags|=1;if(parity==0)flags|=4;if(af)flags|=16;if(result==0)flags|=64;if((result&sign)!=0)flags|=128;if(of)flags|=2048;
            Need(cpu.Step()==4,"Changed nominal instruction clocks.");
            Need(cpu.Eflags==flags,$"Flags op{operation} width{width} A{a:X8} B{b:X8} carry{carry}: {cpu.Eflags:X8}!={flags:X8}");
            uint expected=operation==7?accumulator:(baseAx&~mask)|result;
            Need(cpu.GetRegister(Register.Ax)==expected,"ALU result/CMP register retention differs.");
            Need(!cpu.Halted&&!cpu.TripleFaulted&&cpu.Eip==entry+(uint)(prefix+1+width),"Actual instruction did not retire normally.");
        }
    }
    /// <summary>Registers both existing CPU models, all three widths and all eight ALU families.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        foreach(var modelValue in new[]{X86CpuModel.Intel486Dx,X86CpuModel.IntelP54cB5Subset})foreach(int widthValue in new[]{1,2,4})for(int operationValue=0;operationValue<8;operationValue++)
        {var model=modelValue;int width=widthValue,operation=operationValue;test($"Arithmetic flags BigInteger model{model} width{width} op{operation}",()=>Case(model,width,operation));}
    }
}
