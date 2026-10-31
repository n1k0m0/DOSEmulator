// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

/// <summary>Full scalar comparisons for actual profiled counter/prefix families and exact backedge budgets.</summary>
internal static class DecodedPrefixCounterTests
{
    /// <summary>Requires actual scalar equivalence or an independently specified admission limit.</summary>
    private static void Need(bool value,string why){if(!value)throw new Exception(why);}

    /// <summary>Reuses genuine mode/paging/V86 fixtures and compares every CPU/RAM member after each vector.</summary>
    private sealed class Pair
    {
        private readonly DecodedBatchTests.Rig _a,_b;
        private readonly StateBlock _left,_right;
        private readonly uint _code,_data;
        public readonly bool Wide;

        /// <summary>Creates separate actual-mode machines and warms the precise independent stack source/destination pages.</summary>
        public Pair(int mode,X86CpuModel model)
        {
            _a=DecodedBatchTests.Make(mode,[],model);_b=DecodedBatchTests.Make(mode,[],model);Wide=mode is 2 or 4;
            foreach(var rig in new[]{_a,_b})
            {
                uint scratch=rig.Cpu.GetSegmentBase(Segment.Cs)+rig.Start+0x400;
                new byte[]{0x50,0x58,0x58,0x50}.CopyTo(rig.Memory.Ram,(int)scratch);rig.Cpu.Eip=rig.Start+0x400;
                rig.Cpu.Step();rig.Cpu.Step();rig.Cpu.Step();rig.Cpu.Step();rig.Cpu.Eip=rig.Start;
                // A 16-bit BP address truncates a 32-bit stack pointer. Warm that
                // independent source page as well; retain the actual wide stack.
                uint originalStack=rig.Cpu.GetRegister(Register.Sp);
                if(originalStack>65535)
                {
                    rig.Cpu.SetRegister(Register.Sp,originalStack&65535);rig.Cpu.Eip=rig.Start+0x400;
                    rig.Cpu.Step();rig.Cpu.Step();rig.Cpu.Step();rig.Cpu.Step();
                    rig.Cpu.SetRegister(Register.Sp,originalStack);rig.Cpu.Eip=rig.Start;
                }
            }
            _left=_a.Cpu.SaveState();_right=_b.Cpu.SaveState();
            _code=_a.Cpu.GetSegmentBase(Segment.Cs)+_a.Start;_data=_a.Cpu.GetSegmentBase(Segment.Ds)+0x7000;
        }

        /// <summary>Runs real instructions against current operands and compares every CPU field and RAM byte with scalar execution.</summary>
        public void Run(byte[] bytes,int expected=3,int maximum=64,uint value=0x7fffffffu,ulong? deadline=null,bool framePointer=false,uint? si=null,uint? cx=null,uint? arithmeticFlags=null)
        {
            _a.Cpu.LoadState(_left);_b.Cpu.LoadState(_right);
            if(arithmeticFlags is uint af)
            {_a.Cpu.Eflags=(_a.Cpu.Eflags&~0x8d5u)|(af&0x8d5);_b.Cpu.Eflags=(_b.Cpu.Eflags&~0x8d5u)|(af&0x8d5);}
            if(framePointer){_a.Cpu.SetRegister(Register.Bp,_a.Cpu.GetRegister(Register.Sp));_b.Cpu.SetRegister(Register.Bp,_b.Cpu.GetRegister(Register.Sp));}
            if(si is uint source){_a.Cpu.SetRegister(Register.Si,source);_b.Cpu.SetRegister(Register.Si,source);}
            if(cx is uint counter){_a.Cpu.SetRegister(Register.Cx,counter);_b.Cpu.SetRegister(Register.Cx,counter);}
            byte[] program=[..bytes,0x90,0x90,0xf4];
            program.CopyTo(_a.Memory.Ram,(int)_code);program.CopyTo(_b.Memory.Ram,(int)_code);
            _a.Memory.WriteDword(_data,value);_b.Memory.WriteDword(_data,value);
            var clock=new DeviceScheduler();var owners=new Dictionary<object,string>();
            string before=JsonSerializer.Serialize(clock.SaveState(owners),StateValues.Json);
            int count=deadline is ulong end?_b.Cpu.TryRunDecodedBatch(maximum,clock,100000000,end):_b.Cpu.TryRunDecodedBatch(maximum);
            Need(count==expected,$"Retirements {count}, expected {expected}: {Convert.ToHexString(bytes)}");
            for(int i=0;i<count;i++)Need(_a.Cpu.Step()==4,"Scalar clocks changed.");
            DecodedBatchTests.Equal(_a,_b);
            Need(before==JsonSerializer.Serialize(clock.SaveState(owners),StateValues.Json),"Admission changed scheduler.");
        }
    }

    /// <summary>Registers independent width/flag/count/address/loop/deadline vectors in both CPU models and all seven existing contexts.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        foreach(var modelValue in new[]{X86CpuModel.Intel486Dx,X86CpuModel.IntelP54cB5Subset})
        for(int modeValue=0;modeValue<7;modeValue++)
        {
            var model=modelValue;int mode=modeValue;
            test($"ProfiledOps exact TEST data-before-immediate and FF PUSH restart {model} mode{mode}",()=>
            {
                var p=new Pair(mode,model);
                foreach(byte[] operand in new byte[][]{[],[0x66]})
                foreach(byte[] address in new byte[][]{[],[0x67]})
                {
                    bool wide=p.Wide^(operand.Length!=0),addressWide=p.Wide^(address.Length!=0);
                    byte[] offset=addressWide?[0,0x70,0,0]:[0,0x70];
                    foreach(byte opcode in new byte[]{0xf6,0xf7})
                    {
                        byte[] immediate=opcode==0xf6?[0xa5]:wide?[0x12,0x34,0x56,0x98]:[0x56,0x98];
                        p.Run([..operand,..address,0x3e,opcode,(byte)(addressWide?5:6),..offset,..immediate]);
                    }
                    p.Run([..operand,..address,0xff,(byte)(addressWide?0x35:0x36),..offset]);
                    for(int register=0;register<8;register++)p.Run([..operand,..address,0xff,(byte)(0xf0|register)]);
                    if(addressWide)p.Run([..operand,..address,0xff,0x34,0x24]);
                    else p.Run([..operand,..address,0xff,0x76,0],framePointer:true);
                }
            });
            test($"ProfiledOps exact SHLD SHRD values counts flags and prefix state {model} mode{mode}",()=>
            {
                var p=new Pair(mode,model);
                foreach(byte[] operand in new byte[][]{[],[0x66]})
                foreach(byte opcode in new byte[]{0xa4,0xa5,0xac,0xad})
                foreach(byte count in new byte[]{0,1,2,15,16,17,31,32,33,63,255})
                {
                    byte[] tail=(opcode&1)==0?[count]:[];
                    foreach(byte modrm in new byte[]{0xc0,0xc1,0xc8,0xd3,0xec,0xfa,0xff})
                    foreach(uint initial in new uint[]{0,0x8d5})
                        p.Run([..operand,0x0f,opcode,modrm,..tail],cx:count,arithmeticFlags:initial);
                }
            });
            test($"DecodedPrefix independent widths and current full state {model} mode{mode}",()=>
            {
                var p=new Pair(mode,model);
                foreach(byte[] operand in new byte[][]{[],[0x66],[0x66,0x66]})
                foreach(byte[] address in new byte[][]{[],[0x67],[0x67,0x67]})
                {
                    bool wide=p.Wide^(operand.Length!=0),addressWide=p.Wide^(address.Length!=0);
                    byte[] offset=addressWide?[0,0x70,0,0]:[0,0x70];
                    byte[] immediate=wide?[0x78,0x56,0x34,0x92]:[0x78,0x92];
                    byte[] prefix=[..operand,..address,0x3e];
                    p.Run([..prefix,0xb8,..immediate]);
                    p.Run([..prefix,0xa1,..offset]);p.Run([..prefix,0xa3,..offset]);
                    p.Run([..prefix,0x8b,(byte)(addressWide?0x0d:0x0e),..offset]);
                    p.Run([..prefix,0x83,(byte)(addressWide?0x05:0x06),..offset,0xff]);
                    p.Run([..prefix,0x8d,(byte)(addressWide?0x05:0x06),..offset]);
                    p.Run([..prefix,0xd1,0xe0]);p.Run([..prefix,0x6b,0xc0,0xff]);
                    p.Run([..prefix,0x50]);p.Run([..prefix,0x58]);
                }
                // Every state transition must reset the previous instruction's effective prefixes.
                p.Run([0x66,0x40,0x40,0x66,0x48],expected:5);
                p.Run([0x26,0x3e,0x90]);p.Run([0x65,0x64,0x90]);
            });
            test($"DecodedCounter RAM/register flags and widths {model} mode{mode}",()=>
            {
                var p=new Pair(mode,model);
                foreach(byte[] operand in new byte[][]{[],[0x66]})
                foreach(byte opcode in new byte[]{0xfe,0xff})
                foreach(int operation in new[]{0,1})
                {
                    byte[] offset=p.Wide?[0,0x70,0,0]:[0,0x70];
                    byte modrm=(byte)((p.Wide?5:6)|(operation<<3));
                    foreach(uint value in new uint[]{0,1,0x7f,0x80,0x7fff,0x8000,0x7fffffff,0x80000000,uint.MaxValue})
                    foreach(uint initial in new uint[]{0,0x8d5})
                        p.Run([..operand,opcode,modrm,..offset],value:value,arithmeticFlags:initial);
                    for(int register=0;register<8;register++)p.Run([..operand,opcode,(byte)(0xc0|(operation<<3)|register)]);
                }
            });
            test($"DecodedExtras unary, current memory extension, stores and frame stack {model} mode{mode}",()=>
            {
                var p=new Pair(mode,model);
                foreach(byte[] operand in new byte[][]{[],[0x66]})
                foreach(byte[] address in new byte[][]{[],[0x67]})
                {
                    bool wide=p.Wide^(operand.Length!=0),addressWide=p.Wide^(address.Length!=0);
                    byte[] offset=addressWide?[0,0x70,0,0]:[0,0x70];byte modrm=(byte)(addressWide?5:6);
                    byte[] immediate=wide?[0x78,0x56,0x34,0x92]:[0x78,0x92];
                    byte[] prefix=[..operand,..address,0x3e];
                    p.Run([..prefix,0xc6,modrm,..offset,0xff]);p.Run([..prefix,0xc7,modrm,..offset,..immediate]);
                    foreach(byte operation in new byte[]{2,3})
                    foreach(byte opcode in new byte[]{0xf6,0xf7})
                    {
                        p.Run([..prefix,opcode,(byte)(modrm|operation<<3),..offset]);
                        for(int register=0;register<8;register++)p.Run([..prefix,opcode,(byte)(0xc0|operation<<3|register)]);
                    }
                    p.Run([..prefix,0xf6,0xc4,0x81]);p.Run([..prefix,0xf7,0xc0,..immediate]);
                    foreach(byte opcode in new byte[]{0xb6,0xb7,0xbe,0xbf})
                    foreach(uint value in new uint[]{0,0x7f,0x80,0xff,0x7fff,0x8000,0xffff,uint.MaxValue})
                        p.Run([..prefix,0x0f,opcode,modrm,..offset],value:value);
                    p.Run([..prefix,0x68,..immediate]);p.Run([..prefix,0x6a,0xff]);p.Run([..prefix,0xc9],framePointer:true);
                }
            });
            test($"DecodedPrefix length fallbacks and exact scheduler admission {model} mode{mode}",()=>
            {
                var p=new Pair(mode,model);
                p.Run([..Enumerable.Repeat((byte)0x66,14),0x90]);
                p.Run([..Enumerable.Repeat((byte)0x66,15),0x90],expected:0);
                foreach(byte[] prefix in new byte[][]{[0xf0],[0xf2],[0xf3],[0x66,0xf0],[0x67,0xf3]})
                    p.Run([..prefix,0x90],expected:0);
                foreach(int budget in new[]{0,1,2,3,16})
                    p.Run([0x66,0x40],expected:budget<2?0:Math.Min(3,budget),deadline:(ulong)budget*40);
            });
            test($"DecodedProfiled long guarded regions and partial current-data exits {model} mode{mode}",()=>
            {
                var p=new Pair(mode,model);byte[] offset=p.Wide?[0,0x70,0,0]:[0,0x70];
                byte modrm=(byte)(p.Wide?5:6);byte[] immediate=p.Wide?[0x78,0x56,0x34,0x92]:[0x78,0x92];
                byte[] force32=p.Wide?[]:[0x66];
                for(int vector=0;vector<32;vector++)
                {
                    p.Run([0xb8,..immediate,0x31,0xc9,0x81,0xc1,..immediate,
                        0x66,0x40,0x83,modrm,..offset,0xff,0xff,(byte)(modrm|8),..offset,
                        0xc7,modrm,..offset,..immediate,0x0f,0xbe,modrm,..offset,
                        ..force32,0xc1,0xea,(byte)(1+vector%31),0xf7,0xd0,0xf7,0xd8,
                        0x6b,0xc0,0xff,0x90,0x33,0xc2,0x0f,0x95,0xc1],expected:17,value:(uint)vector);
                }
                byte[] crossing=p.Wide?[0xff,0x7f,0,0]:[0xff,0x7f];
                byte[] prefix=p.Wide?[]:[0x66];
                p.Run([0x40,0x48,0x40,..prefix,0xa1,..crossing,0x90,0x90,0x90],expected:3);
                p.Run([..prefix,0xa1,..crossing,0x90,0x90,0x90,0x90,0x90],expected:0);
            });
            test($"DecodedProfiledLoop complete iterations, tails, branches and admission exit {model} mode{mode}",()=>
            {
                var p=new Pair(mode,model);
                foreach(int maximum in new[]{0,1,2,3,5,6,7,11,12,13,17,63,64,65,4096})
                {
                    p.Run([0x40,0x42,0x4b,0x90,0x90,0xeb,0xf9],expected:maximum<2?0:maximum,maximum:maximum);
                    foreach(int budget in new[]{0,1,2,5,6,7,13,63,4096})
                        p.Run([0x40,0x42,0x4b,0x90,0x90,0xeb,0xf9],expected:Math.Min(maximum,budget)<2?0:Math.Min(maximum,budget),
                            maximum:maximum,deadline:(ulong)budget*40);
                }
                p.Run([0x40,0x42,0x90,0x90,0x49,0x75,0xf9],expected:18,cx:3);
                if(p.Wide)p.Run([0x89,0x06,0x81,0xee,0,0x50,0,0,0x90,0x90,0x90,0xeb,0xf3],expected:6,si:0x7000);
            });
            test($"DecodedBackedge exact execution budgets {model} mode{mode}",()=>
            {
                var p=new Pair(mode,model);
                foreach(int maximum in new[]{0,1,2,3,7,16,63,64,65,4096})
                {
                    int expected=maximum<2?0:maximum;
                    p.Run([0x40,0xeb,0xfd],expected:expected,maximum:maximum);
                    foreach(int budget in new[]{0,1,2,9,66})
                        p.Run([0x66,0x40,0xeb,0xfc],expected:Math.Min(maximum,budget)<2?0:Math.Min(maximum,budget),
                            maximum:maximum,deadline:(ulong)budget*40);
                }
            });
        }
    }
}
