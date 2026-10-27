// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Numerics;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class ShiftOptimizationTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool ok,string message="Shift compatibility") {if(!ok)throw new Exception(message);}
    // Deliberately independent, bit-at-a-time compatibility oracle. Undefined
    // bits retain the previous interpreter's contract, not a silicon promise.
    private static (uint Value,uint Flags) Reference(uint input,int width,int op,int raw,uint flags)
    {
        int n=raw&31,bits=width*8;if(op is 2 or 3&&width<4)n%=bits+1;
        uint mask=width==4?uint.MaxValue:(1u<<bits)-1,sign=1u<<(bits-1),v=input&mask;
        if(n==0)return(v,flags);
        bool c=(flags&1)!=0;
        for(int k=0;k<n;k++)
        {
            bool hi=(v&sign)!=0,lo=(v&1)!=0;
            v=op switch{0=>((v<<1)|(hi?1u:0))&mask,1=>(v>>1)|(lo?sign:0),2=>((v<<1)|(c?1u:0))&mask,
                3=>(v>>1)|(c?sign:0),4 or 6=>(v<<1)&mask,5=>v>>1,_=>(v>>1)|(v&sign)};
            c=op is 0 or 2 or 4 or 6?hi:lo;
        }
        flags=(flags&~1u)|(c?1u:0);
        if(op>=4)flags=(flags&~0xc4u)|(v==0?0x40u:0)|((v&sign)!=0?0x80u:0)|((BitOperations.PopCount(v&255)&1)==0?4u:0);
        if(n==1)
        {
            bool o=op switch{0 or 2 or 4 or 6=>((v&sign)!=0)^c,1 or 3=>((v&sign)!=0)^((v&(sign>>1))!=0),5=>(input&sign)!=0,_=>false};
            flags=(flags&~0x800u)|(o?0x800u:0);
        }
        return(v,flags);
    }
    /// <summary>Registers the named shift optimization regression cases with the common runner.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        foreach(int width in new[]{1,2,4})foreach(int op in Enumerable.Range(0,8))
            test($"optimized shift compatibility width={width} op={op}, all 256 counts and flag seeds",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);uint mask=width==4?uint.MaxValue:(1u<<(8*width))-1;
                uint[] values=[0,1,2,3,0x7f,0x80,0x8001,0x7fffffff,0x80000000,0xffffffff,0x98abcdef];
                foreach(bool memory in new[]{false,true})foreach(int raw in Enumerable.Range(0,256))foreach(uint input in values)foreach(uint flags in new uint[]{2,3,0x8d6,0x8d7})
                {
                    byte[] prefix=width==2?[0x66]:[];
                    byte[] operand=memory?[(byte)(op*8+5),0,0x10,3,0]:[(byte)(0xc0+op*8)];
                    byte[] code=[..prefix,(byte)(width==1?0xc0:0xc1),..operand,(byte)raw];code.CopyTo(m.Ram,0x2000);
                    c.Eip=0x2000;c.Eflags=flags;c.SetRegister(Register.Ax,input);m.WriteDword(0x31000,input);
                    var expected=Reference(input,width,op,raw,flags);c.Step();
                    uint actual=memory?m.ReadDword(0x31000):c.GetRegister(Register.Ax);
                    Check((actual&mask)==expected.Value && (actual&~mask)==(input&~mask) && c.Eflags==expected.Flags,
                        $"w={width},op={op},count={raw},input={input:X8},flags={flags:X}: {actual:X8}/{c.Eflags:X} vs {expected.Value:X8}/{expected.Flags:X}");
                }
            });
        foreach(int width in new[]{1,2,4})foreach(int op in Enumerable.Range(0,8))
            test($"CL/destination alias keeps old count/value width={width} op={op}",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);uint mask=width==4?uint.MaxValue:(1u<<(8*width))-1;
                foreach(int raw in Enumerable.Range(0,256))
                {
                    uint input=0x98765400|(uint)raw;byte[] code=[..(width==2?new byte[]{0x66}:[]),(byte)(width==1?0xd2:0xd3),(byte)(0xc1+op*8)];
                    code.CopyTo(m.Ram,0x2000);c.Eip=0x2000;c.Eflags=0x8d7;c.SetRegister(Register.Cx,input);
                    var expected=Reference(input,width,op,raw,0x8d7);c.Step();
                    Check((c.GetRegister(Register.Cx)&mask)==expected.Value && (c.GetRegister(Register.Cx)&~mask)==(input&~mask) && c.Eflags==expected.Flags);
                }
            });
        foreach(int count in new[]{0,1,31,32,255})
            test($"memory shift count={count} retains PF write permission before data effects",()=>
            {
                var(c,m)=PagingTests.Create([0x90]);PagingTests.Pte(m,0x31000,0x31000,1);m.WriteDword(0x31000,0x89abcdef);
                // Supervisor write protection is required for a read-only CPL0 PTE.
                c.SetRegister(Register.Ax,c.Cr0|0x10000);new byte[]{0x0f,0x22,0xc0}.CopyTo(m.Ram,0x2200);c.Eip=0x2200;c.Step();
                new byte[]{0xc1,0x25,0,0x10,3,0,(byte)count}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;c.Eflags=0x8d7;Fault(c,m,14,3);
                Check(m.ReadDword(0x31000)==0x89abcdef);
            });
    }
}
