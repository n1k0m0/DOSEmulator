// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

/// <summary>Independent numeric/stack expectations and whole scalar futures for profiled game and Win16 instruction families.</summary>
internal static class DecodedWorkloadOpsTests
{
    private static readonly byte[][] Prefixes=[[],[0x66],[0x67],[0x66,0x67]];
    /// <summary>Requires an independently predicted effect or a complete state/purity comparison.</summary>
    private static void Need(bool condition,string why){if(!condition)throw new Exception(why);}
    /// <summary>Returns the real effective operand width for this fixture and default-relative prefix set.</summary>
    private static int Width(DecodedBatchTests.Rig rig,byte[] prefixes)=>
        ((!rig.Cpu.Virtual8086 && rig.Cpu.GetSegmentCache(Segment.Cs).Default32) ^ prefixes.Contains((byte)0x66))?4:2;
    /// <summary>Builds an actual direct DS memory encoding under its independent address-size prefix.</summary>
    private static byte[] MemoryCode(DecodedBatchTests.Rig rig,byte[] prefixes,byte opcode,int operation)
    {
        bool wide=(!rig.Cpu.Virtual8086 && rig.Cpu.GetSegmentCache(Segment.Cs).Default32)^prefixes.Contains((byte)0x67);
        return wide?[..prefixes,opcode,(byte)(operation<<3|5),0,0x70,0,0]:[..prefixes,opcode,(byte)(operation<<3|6),0,0x70];
    }
    /// <summary>Primes only derived plans, then restores every original CPU member and RAM byte.</summary>
    private static void Prime(DecodedBatchTests.Rig rig)
    {
        var state=rig.Cpu.SaveState();byte[] ram=(byte[])rig.Memory.Ram.Clone();
        rig.Cpu.TryRunDecodedBatch(32);ram.CopyTo(rig.Memory.Ram,0);rig.Cpu.LoadState(state);
    }
    /// <summary>Creates exact paired fixtures and warms the actual stack through public guest stack accesses.</summary>
    private static (DecodedBatchTests.Rig A,DecodedBatchTests.Rig B) Pair(int mode,X86CpuModel model,byte[] code)
    {
        var a=DecodedBatchTests.Make(mode,code,model);var b=DecodedBatchTests.Make(mode,code,model);
        foreach(var rig in new[]{a,b}){rig.Cpu.Push(0xdeadbeef,4);Need(rig.Cpu.Pop(4)==0xdeadbeef,"Stack warming failed.");}
        return(a,b);
    }
    /// <summary>Runs exactly the publicly returned element count with scalar Step and compares every CPU/RAM field.</summary>
    private static void Run(DecodedBatchTests.Rig a,DecodedBatchTests.Rig b,int expected)
    {
        int actual=b.Cpu.TryRunDecodedBatch(32);Need(actual==expected,$"Expected {expected} elements, got {actual} at {b.Cpu.Position}.");
        for(int i=0;i<actual;i++)Need(a.Cpu.Step()==4,"Changed clock count.");DecodedBatchTests.Equal(a,b);
    }
    /// <summary>Replaces only the current instruction bytes after mode creation, with no architectural shortcut.</summary>
    private static void Install(DecodedBatchTests.Rig rig,byte[] code)
    {
        uint at=unchecked(rig.Cpu.GetSegmentBase(Segment.Cs)+rig.Start);code.CopyTo(rig.Memory.Ram,(int)at);
        rig.Memory.Ram[at+(uint)code.Length]=0xf4;rig.Cpu.Eip=rig.Start;
    }
    /// <summary>Reads a register alias independently from the implementation's private register helpers.</summary>
    private static uint RegisterValue(X86Cpu cpu,int register,int width)
    {
        uint value=cpu.GetRegister((Register)(width==1?register&3:register));
        return width==1?(value>>(register<4?0:8))&255:width==2?value&65535:value;
    }
    /// <summary>Tests all segment selectors in registers and actual word destinations, retaining neighboring memory bytes.</summary>
    private static void SegmentReads(int mode,X86CpuModel model)
    {
        foreach(byte[] prefixes in Prefixes)for(int segment=0;segment<6;segment++)foreach(bool memory in new[]{false,true})
        {
            var(a,b)=Pair(mode,model,[0x90]);byte[] instruction=memory?MemoryCode(a,prefixes,0x8c,segment):[..prefixes,0x8c,(byte)(0xc0|segment<<3)];
            foreach(var rig in new[]{a,b}){Install(rig,[..instruction,0x90]);rig.Cpu.SetRegister(Register.Ax,0xcafe1234);}
            ushort expected=a.Cpu.GetSegment((Segment)segment);Prime(b);Run(a,b,2);
            if(memory)Need(b.Memory.ReadDword(b.Cpu.GetSegmentBase(Segment.Ds)+0x7000)==(0xa19b0000u|expected),"Segment store changed another byte or width.");
            else Need(b.Cpu.GetRegister(Register.Ax)==(Width(b,prefixes)==2?0xcafe0000u|expected:expected),"Segment register result width differs.");
        }
    }
    /// <summary>Checks selector pushes, operand/SS widths and eager FS/GS checkpoints against complete scalar state.</summary>
    private static void SegmentPushes(int mode,X86CpuModel model)
    {
        foreach(byte[] prefixes in Prefixes)for(int segment=0;segment<6;segment++)
        {
            byte[] opcode=segment<4?[(byte)(6+segment*8)]:[0x0f,segment==4?(byte)0xa0:(byte)0xa8];
            var(a,b)=Pair(mode,model,[..prefixes,..opcode,0x90]);int width=Width(a,prefixes);
            uint mask=a.Cpu.GetSegmentCache(Segment.Ss).Default32?uint.MaxValue:65535,esp=a.Cpu.GetRegister(Register.Sp);
            uint expectedPointer=((esp&mask)-(uint)width)&mask;ushort selector=a.Cpu.GetSegment((Segment)segment);Prime(b);Run(a,b,2);
            Need(b.Cpu.GetRegister(Register.Sp)==((esp&~mask)|expectedPointer),"Selector push used operand width for SS addressing.");
            uint address=b.Cpu.GetSegmentBase(Segment.Ss)+expectedPointer;
            Need((width==2?b.Memory.ReadWord(address):b.Memory.ReadDword(address))==selector,"Selector push value or high word differs.");
        }
    }
    /// <summary>Checks nesting-zero frames including ignored high nesting bits, allocation wrapping and independent BP/SP widths.</summary>
    private static void EnterFrames(int mode,X86CpuModel model)
    {
        foreach(byte[] prefixes in Prefixes)foreach(ushort allocation in new ushort[]{0,1,0x111,0xffff})foreach(byte nesting in new byte[]{0,0x80})
        {
            var(a,b)=Pair(mode,model,[..prefixes,0xc8,(byte)allocation,(byte)(allocation>>8),nesting,0x90]);
            int width=Width(a,prefixes);uint mask=a.Cpu.GetSegmentCache(Segment.Ss).Default32?uint.MaxValue:65535;
            uint esp=a.Cpu.GetRegister(Register.Sp),oldBp=a.Cpu.GetRegister(Register.Bp),frame=((esp&mask)-(uint)width)&mask,final=(frame-allocation)&mask;
            Prime(b);Run(a,b,2);
            Need(b.Cpu.GetRegister(Register.Sp)==((esp&~mask)|final),"ENTER stack addressing or allocation wrap differs.");
            Need(b.Cpu.GetRegister(Register.Bp)==(width==2?(oldBp&0xffff0000)|(frame&65535):frame),"ENTER frame pointer operand width differs.");
        }
    }
    /// <summary>Checks full products and retained undefined arithmetic flags over signed/unsigned aliases and width extremes.</summary>
    private static void WideMultiply(int mode,X86CpuModel model)
    {
        foreach(byte[] prefixes in Prefixes)foreach(byte opcode in new byte[]{0xf6,0xf7})foreach(int operation in new[]{4,5})foreach(int source in new[]{0,2,4,7})
        {
            var(a,b)=Pair(mode,model,[..prefixes,opcode,(byte)(0xc0|operation<<3|source),0x90]);int width=opcode==0xf6?1:Width(a,prefixes);Prime(b);
            foreach(uint input in new uint[]{0,1,0x80008080,0xffffffff})
            {
                foreach(var rig in new[]{a,b})
                {
                    rig.Cpu.Eip=rig.Start;rig.Cpu.SetRegister(Register.Ax,input^0x7f000001);rig.Cpu.SetRegister(Register.Dx,0x98765432);
                    rig.Cpu.SetRegister((Register)(width==1?source&3:source),input);
                }
                uint x=RegisterValue(a.Cpu,0,width),y=RegisterValue(a.Cpu,source,width),flags=a.Cpu.Eflags;
                // Interpret the original operand width independently from the CPU's private signed helper.
                long signed(uint value)=>width==1?(sbyte)value:width==2?(short)value:(int)value;
                ulong product=operation==4?(ulong)x*y:unchecked((ulong)(signed(x)*signed(y)));uint mask=width==1?255u:width==2?65535u:uint.MaxValue;
                bool overflow=operation==4?product>>(width*8)!=0:unchecked((long)product)!=signed((uint)product);
                Run(a,b,2);
                Need((b.Cpu.Eflags&0x801)==(overflow?0x801u:0),"MUL/IMUL CF/OF differs from full product.");
                Need((b.Cpu.Eflags&~0x801u)==(flags&~0x801u),"MUL/IMUL changed retained undefined flags.");
                Need(width==1?RegisterValue(b.Cpu,0,2)==(product&65535):RegisterValue(b.Cpu,0,width)==(product&mask)&&RegisterValue(b.Cpu,2,width)==((product>>(width*8))&mask),"MUL/IMUL result register placement differs.");
            }
        }
    }
    /// <summary>Checks changing current register targets, including CALL SP's original value and exact pushed return address.</summary>
    private static void NearRegisters(int mode,X86CpuModel model)
    {
        foreach(byte[] prefixes in Prefixes)foreach(int operation in new[]{2,4})foreach(int source in new[]{0,1,4,7})
        {
            var(a,b)=Pair(mode,model,[..prefixes,0xff,(byte)(0xc0|operation<<3|source)]);int width=Width(a,prefixes);
            foreach(var rig in new[]{a,b})if(source!=4)rig.Cpu.SetRegister((Register)source,rig.Start+0x80);
            Prime(b);uint target=RegisterValue(a.Cpu,source,width),esp=a.Cpu.GetRegister(Register.Sp);
            if(target>a.Cpu.GetSegmentCache(Segment.Cs).Limit)
            {
                Need(b.Cpu.TryRunDecodedBatch(32)==0,"CALL SP widened a short CS limit.");DecodedBatchTests.Equal(a,b);continue;
            }
            Run(a,b,1);
            Need(b.Cpu.Eip==target,"Indirect register target was stale or CALL used post-push SP.");
            if(operation==2)
            {
                uint pointer=b.Cpu.GetRegister(Register.Sp)&(b.Cpu.GetSegmentCache(Segment.Ss).Default32?uint.MaxValue:65535);
                uint value=width==2?b.Memory.ReadWord(b.Cpu.GetSegmentBase(Segment.Ss)+pointer):b.Memory.ReadDword(b.Cpu.GetSegmentBase(Segment.Ss)+pointer);
                Need(value==(width==2?(b.Start+(uint)prefixes.Length+2)&65535:b.Start+(uint)prefixes.Length+2),"Indirect CALL return bytes differ.");
            }
            else Need(b.Cpu.GetRegister(Register.Sp)==esp,"Indirect JMP touched the stack.");
        }
    }
    /// <summary>Changes a warm plain-RAM target after priming, then checks exact target and source-read/push TLB ordering.</summary>
    private static void NearMemory(int mode,X86CpuModel model)
    {
        foreach(byte[] prefixes in Prefixes)foreach(int operation in new[]{2,4})
        {
            var(a,b)=Pair(mode,model,[0x90]);byte[] code=MemoryCode(a,prefixes,0xff,operation);
            foreach(var rig in new[]{a,b}){Install(rig,code);rig.Memory.WriteDword(rig.Cpu.GetSegmentBase(Segment.Ds)+0x7000,rig.Start+0x40);}
            Prime(b);
            foreach(var rig in new[]{a,b})rig.Memory.WriteDword(rig.Cpu.GetSegmentBase(Segment.Ds)+0x7000,rig.Start+0x80);
            Run(a,b,1);Need(b.Cpu.Eip==b.Start+0x80,"Indirect memory target reused the priming value.");
        }
    }
    /// <summary>Own mapped operand with observable byte reads/writes, never a host endpoint.</summary>
    private sealed class OperandPeer(uint value):IMemoryDevice
    {
        public int Reads,Writes;
        /// <summary>Returns the supplied original near target and records actual bus reads.</summary>
        public byte ReadByte(uint offset){Reads++;return(byte)(value>>(int)(offset*8));}
        /// <summary>Records only actual original word-store fallback bytes.</summary>
        public void WriteByte(uint offset,byte data){Writes++;}
    }
    /// <summary>Changes one explicit segment-cache fixture input without bypassing any future CPU access.</summary>
    private static void SegmentInput(DecodedBatchTests.Rig rig,Segment segment,Func<SegmentDescriptorCache,SegmentDescriptorCache> change)
    {
        var state=rig.Cpu.SaveState();var caches=state.Fields["_segmentCaches"].Deserialize<SegmentDescriptorCache[]>(StateValues.Json)!;
        caches[(int)segment]=change(caches[(int)segment]);state.Fields["_segmentCaches"]=JsonSerializer.SerializeToElement(caches,StateValues.Json);rig.Cpu.LoadState(state);
    }
    /// <summary>Verifies zero speculative mapped-operand accesses, followed by identical real scalar bus effects.</summary>
    private static void MappedOperand(int mode,bool call)
    {
        var(a,b)=Pair(mode,X86CpuModel.IntelP54cB5Subset,[0x90]);byte[] code=MemoryCode(a,[],call?(byte)0xff:(byte)0x8c,call?2:3);
        foreach(var rig in new[]{a,b})Install(rig,code);Prime(b);
        var left=new OperandPeer(a.Start+0x80);var right=new OperandPeer(a.Start+0x80);
        a.Memory.Map(a.Cpu.GetSegmentBase(Segment.Ds)+0x7000,4,left,"own mapped operand");b.Memory.Map(b.Cpu.GetSegmentBase(Segment.Ds)+0x7000,4,right,"own mapped operand");
        Need(b.Cpu.TryRunDecodedBatch(32)==0,"Mapped operand admitted.");Need(right.Reads==0&&right.Writes==0,"Admission touched mapped operand.");DecodedBatchTests.Equal(a,b);
        a.Cpu.Step();b.Cpu.Step();DecodedBatchTests.Equal(a,b);
        Need(call?left.Reads==right.Reads&&right.Reads==Width(b,[]):left.Writes==2&&right.Writes==2,"Original mapped byte semantics changed.");
    }
    /// <summary>Requires current target, alias and split-page guards after a cached plan has already succeeded.</summary>
    private static void NearGuards(string reason)
    {
        var(a,b)=Pair(2,X86CpuModel.IntelP54cB5Subset,[0xff,0x15,0,0x70,0,0]);
        foreach(var rig in new[]{a,b})rig.Memory.WriteDword(0x7000,rig.Start+0x80);Prime(b);
        foreach(var rig in new[]{a,b})
        {
            if(reason=="code-alias")rig.Cpu.SetRegister(Register.Sp,rig.Start+4);
            else if(reason=="target-limit")SegmentInput(rig,Segment.Cs,c=>c with{Limit=rig.Start+0x40});
            else {rig.Memory.WriteDword(rig.Start+2,0x7ffe);rig.Memory.WriteDword(0x7ffe,rig.Start+0x80);}
        }
        Need(b.Cpu.TryRunDecodedBatch(32)==0,"Changed near guard ignored: "+reason);DecodedBatchTests.Equal(a,b);
        a.Cpu.Step();b.Cpu.Step();DecodedBatchTests.Equal(a,b);
    }
    /// <summary>Reads a target that the same original CALL subsequently overwrites with its return address.</summary>
    private static void NearSourceStackAlias()
    {
        var(a,b)=Pair(2,X86CpuModel.IntelP54cB5Subset,[0x90]);uint pointer=a.Cpu.GetRegister(Register.Sp)-4;
        byte[] address=BitConverter.GetBytes(pointer);
        foreach(var rig in new[]{a,b}){Install(rig,[0xff,0x15,..address]);rig.Memory.WriteDword(pointer,rig.Start+0x80);}
        Prime(b);Run(a,b,1);Need(b.Cpu.Eip==b.Start+0x80&&b.Memory.ReadDword(pointer)==b.Start+6,"CALL source/stack overlap read after store.");
    }
    /// <summary>Checks ENTER's segment endpoint without inventing translations of unaccessed local storage.</summary>
    private static void EnterUnaccessedLocals()
    {
        var(a,b)=Pair(4,X86CpuModel.IntelP54cB5Subset,[0xc8,0x20,0x10,0,0x90]);Prime(b);
        foreach(var rig in new[]{a,b})PagingTests.Pte(rig.Memory,0x16000,0,0);
        Run(a,b,2);Need(b.Cpu.GetRegister(Register.Sp)==0x16fdc,"ENTER translated or changed unaccessed locals.");
    }
    /// <summary>Falls back before ENTER's late endpoint fault and retains the actual SS fault frame and complete scalar future.</summary>
    private static void EnterEndpointFault()
    {
        var(a,b)=Pair(2,X86CpuModel.IntelP54cB5Subset,[0xc8,0,1,0]);Prime(b);
        foreach(var rig in new[]{a,b})SegmentInput(rig,Segment.Ss,c=>c with{Access=0x97,Limit=0x17f00});
        Need(b.Cpu.TryRunDecodedBatch(32)==0,"ENTER late segment fault speculated.");DecodedBatchTests.Equal(a,b);
        byte? left=null,right=null;a.Cpu.InterruptEntered+=v=>left=v;b.Cpu.InterruptEntered+=v=>right=v;
        a.Cpu.Step();b.Cpu.Step();DecodedBatchTests.Equal(a,b);
        Need(left==12&&right==12&&b.Cpu.Eip==0x6000&&b.Memory.ReadDword(0x17ff4)==b.Start,"ENTER SS fault frame or original restart IP differs.");
    }
    /// <summary>Registers mode/model families; each group contains independent encodings, inputs and complete-state futures.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        foreach(int modeValue in new[]{0,2,4,6})foreach(bool callValue in new[]{false,true})
        {int mode=modeValue;bool call=callValue;test($"DecodedWorkload mapped operand purity mode{mode} call{call}",()=>MappedOperand(mode,call));}
        foreach(string reasonValue in new[]{"code-alias","target-limit","split-page"})
        {string reason=reasonValue;test("DecodedWorkload current near guard "+reason,()=>NearGuards(reason));}
        test("DecodedWorkload near source is overwritten only after target read",NearSourceStackAlias);
        test("DecodedWorkload ENTER skips unaccessed nonpresent locals",EnterUnaccessedLocals);
        test("DecodedWorkload ENTER late endpoint fault retains scalar SS frame",EnterEndpointFault);
        foreach(var modelValue in new[]{X86CpuModel.Intel486Dx,X86CpuModel.IntelP54cB5Subset})for(int modeValue=0;modeValue<7;modeValue++)
        {
            var model=modelValue;int mode=modeValue;
            test($"DecodedWorkload MOV segment reads model{model} mode{mode}",()=>SegmentReads(mode,model));
            test($"DecodedWorkload selector pushes model{model} mode{mode}",()=>SegmentPushes(mode,model));
            test($"DecodedWorkload ENTER zero nesting model{model} mode{mode}",()=>EnterFrames(mode,model));
            test($"DecodedWorkload MUL IMUL wide product model{model} mode{mode}",()=>WideMultiply(mode,model));
            test($"DecodedWorkload current near register targets model{model} mode{mode}",()=>NearRegisters(mode,model));
            test($"DecodedWorkload current near memory targets model{model} mode{mode}",()=>NearMemory(mode,model));
        }
        foreach(byte[] bytes in new byte[][]{[0xc8,0,0,1],[0xc8,0,0,31],[0x8c,0xf0],[0xff,0xd8],[0xff,0xe8],[0xf7,0x25,0,0x70,0,0],[0xf7,0xc8],[0xf0,0xff,0xd0],[0xf3,0xc8,0,0,0]})
        {
            byte[] code=bytes;
            test("DecodedWorkload reserved control or nesting remains scalar "+Convert.ToHexString(code),()=>
            {
                var(a,b)=Pair(2,X86CpuModel.IntelP54cB5Subset,[..code,0x90]);Need(b.Cpu.TryRunDecodedBatch(32)==0,"Unsafe or unsupported control form was admitted.");DecodedBatchTests.Equal(a,b);
            });
        }
    }
}
