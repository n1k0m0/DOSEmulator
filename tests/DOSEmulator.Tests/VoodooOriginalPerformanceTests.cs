// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Hardware;

/// <summary>Original chip-address contracts, bounded special S/T conversion and physically aliased frontbuffer cache oracles.</summary>
internal static class VoodooOriginalPerformanceTests
{
    private const uint Base=0xd0000000;
    /// <summary>Requires an independent address/color/count expectation.</summary>
    private static void Check(bool value){if(!value)throw new Exception("Original SST chip/scanout contract differs");}
    /// <summary>Registers all sixteen chip masks and both actual setup address layouts, plus complete scanout alias boundaries.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        test("Voodoo unused iterated RGB is independent across every supported combiner selector",()=>
        {
            const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic;
            var used=typeof(VoodooGraphics).GetMethod("UsesIteratedRgb",flags)!.CreateDelegate<Func<uint,bool>>();
            var combine=typeof(VoodooGraphics).GetMethod("Combine",flags)!.CreateDelegate<Func<int,int,int,int,int,uint,int>>();
            int skipped=0;
            for(uint control=0;control<512;control++)for(uint selector=0;selector<4;selector++)foreach(bool constant in new[]{false,true})
            {
                if((control>>2&7)>4)continue;
                uint path=selector|(constant?16u:0)|control<<8;
                if(used(path))continue;
                skipped++;
                int? expected=null;
                foreach(int iterated in new[]{0,1,37,128,255})
                {
                    int other=selector==0?iterated:selector==1?34:147,local=constant?99:iterated;
                    int result=combine(other,local,73,111,222,control);
                    if(expected is not null)Check(result==expected);expected=result;
                }
            }
            Check(skipped>0);
        });
        test("Voodoo perspective division preserves every signed narrow wide and overflow result",()=>
        {
            var method=typeof(VoodooGraphics).GetMethod("PerspectiveDivide",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
            var divide=method.CreateDelegate<Func<long,long,long>>();
            long[] coordinates=[long.MinValue,(long.MinValue>>20)-1,long.MinValue>>20,(long.MinValue>>20)+1,-65537,-1,0,1,65537,
                (long.MaxValue>>20)-1,long.MaxValue>>20,(long.MaxValue>>20)+1,long.MaxValue];
            long[] denominators=[long.MinValue,-65537,-3,-1,1,3,65537,1L<<32,long.MaxValue];
            foreach(long coordinate in coordinates)foreach(long w in denominators)CompareDivide(divide,coordinate,w);
            ulong seed=0x1ae7058b2cda639f;
            for(int i=0;i<20000;i++)
            {
                seed=unchecked(seed*6364136223846793005+1442695040888963407);long coordinate=unchecked((long)seed)>>(i%33);
                seed=unchecked(seed*6364136223846793005+1442695040888963407);long w=unchecked((long)seed)|1;
                CompareDivide(divide,coordinate,w);
            }
        });
        test("Voodoo resource and complete DWORD queries avoid per-access allocations",()=>
        {
            var r=new VoodooGraphicsTests.Rig();
            for(int i=0;i<4096;i++){r.Memory.IsFixedWindowAvailable(Base,0x1000000);r.Memory.IsImmediateDwordWrite(Base+0x20);r.Memory.TransactionReadyAt(Base+0x80,true);}
            long before=GC.GetAllocatedBytesForCurrentThread();int accepted=0;
            for(int i=0;i<4096;i++)
            {
                if(r.Memory.IsFixedWindowAvailable(Base,0x1000000))accepted++;
                if(r.Memory.IsImmediateDwordWrite(Base+0x20))accepted++;
                if(r.Memory.TransactionReadyAt(Base+0x80,true) is null)accepted++;
            }
            long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
            Check(accepted==12288&&allocated<=1024);
        });
        foreach(int chip in Enumerable.Range(0,16))foreach(bool floating in new[]{false,true})foreach(bool remap in new[]{false,true})
        {
            int mask=chip;bool fp=floating,alias=remap;
            test($"Voodoo S/T original TREX selection chip {chip} float {floating} remap {remap}",()=>
            {
                var r=new VoodooGraphicsTests.Rig();if(alias)r.Write(0x21c,479u<<22|1);
                foreach(int logical in new[]{0x34,0x38,0x54,0x58,0x74,0x78})
                {
                    int register=logical+(fp?0x80:0),first=fp?0xa0:0x20,index=(register-first)/4;
                    int encoded=alias?first+(index%8*3+index/8)*4:register;
                    r.Memory.WriteDword(Base+(alias?0x200000u:0)+(uint)(mask*1024+encoded),fp?0x3f800000u:0x12345678u);
                    uint expected=mask==0||(mask&2)!=0?(fp?1u<<18:0x12345678):0;
                    Check(r.Card.SaveState().Fields["_registers"][logical/4].GetUInt32()==expected);
                }
            });
        }
        foreach(uint raw in new[]{0x7f800000u,0xff800000u,0x7fc12345u,0xffc12345u})
        test($"Voodoo special S/T {raw:X8} preserves queued bits and selects signed clamped texel",()=>
        {
            var r=new VoodooGraphicsTests.Rig();VoodooGraphicsTests.Triangle(r);r.Write(0x110,0x200);
            r.Write(0x300,10u<<8|1u<<12|1u<<18|1u<<21|1u<<27|192);r.Write(0x304,28u|28u<<6);r.Write(0x104,1u<<27|5);
            r.Memory.WriteDword(Base+0x8e0000,0x07e0f800);r.Memory.WriteDword(Base+0x8e0200,0xffff001f);
            r.Write(0xb4,raw);r.Write(0xb8,raw);r.Write(0x80,0);var saved=r.Card.SaveState();VoodooGraphics.ValidateSavedState(saved);
            Check(saved.Fields["_commands"][0][13].GetUInt32()==raw&&saved.Fields["_commands"][0][14].GetUInt32()==raw);
            r.Finish();r.Write(0x114,0);Check(r.Memory.ReadWord(Base+0x400000)==((raw&0x80000000)==0?0xffff:0xf800));
            Check(r.Card.SaveState().Fields["_registers"][13].GetUInt32()==raw&&r.Card.SaveState().Fields["_nonfiniteParameters"].GetUInt32()==((1u<<11)|(1u<<12)));
        });
        foreach(bool overlap in new[]{false,true})test($"Voodoo complete backbuffer scanout invalidation physical overlap {overlap}",()=>
        {
            var r=new VoodooGraphicsTests.Rig();if(overlap)r.Write(0x218,1u<<11);
            var surface=new VgaFrameBuffer();r.Card.Capture(0,surface);long before=surface.RenderedFrames;
            r.Write(0x110,0x4200);r.Write(0x148,0xff0000);r.Write(0x124,0);r.Finish();var frame=r.Card.Capture(r.Clock.Now,surface);
            Check(surface.RenderedFrames==before+(overlap?1:0));
            Check(frame.ArgbPixels[3*640+128]==(overlap?0xfffe0000u:0xff000000u));
            r.Write(0x128,0);r.Finish();frame=r.Card.Capture(r.Clock.Now,surface);
            Check(surface.RenderedFrames==before+(overlap?2:1)&&frame.ArgbPixels[0]==0xfffe0000u);
            r.Write(0x114,0);r.Memory.WriteWord(Base+0x400000,0x07e0);frame=r.Card.Capture(r.Clock.Now,surface);
            Check(frame.ArgbPixels[0]==0xff00fe00u);
            r.Write(0x228,31u<<24|0x00f800f8);r.Write(0x228,32u<<24|0x00ff00ff);frame=r.Card.Capture(r.Clock.Now,surface);
            Check(frame.ArgbPixels[0]==0xff000000u);
        });
        test("Voodoo auxiliary-plane physical alias invalidates visible color without dropping the actual depth write",()=>
        {
            var r=new VoodooGraphicsTests.Rig();r.Write(0x218,1u<<11);var surface=new VgaFrameBuffer();r.Card.Capture(0,surface);long before=surface.RenderedFrames;
            r.Write(0x118,16);r.Write(0x11c,16);r.Write(0x110,0x400);r.Write(0x130,0xf800);r.Write(0x124,0);r.Finish();var frame=r.Card.Capture(r.Clock.Now,surface);
            Check(surface.RenderedFrames==before+1&&frame.ArgbPixels[6*640+256]==0xfffe0000u);
        });
    }
    /// <summary>Computes the quotient independently with arbitrary precision and the original unchecked signed 64-bit result encoding.</summary>
    private static void CompareDivide(Func<long,long,long> divide,long coordinate,long w)
    {
        var exact=((System.Numerics.BigInteger)coordinate<<20)/w;
        long expected=unchecked((long)(ulong)(exact&ulong.MaxValue));
        Check(divide(coordinate,w)==expected);
    }
}
