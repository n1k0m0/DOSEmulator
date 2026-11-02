// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using System.Text.Json;

/// <summary>Independent per-pixel half-plane coverage, programmed graphics-clock deadlines and saved palette corruption controls.</summary>
internal static class VoodooCoverageTests
{
    private const uint Base=0xd0000000,Lfb=Base+0x400000;
    /// <summary>Requires every declared pixel or deadline to meet its independent oracle.</summary>
    private static void Check(bool value){if(!value)throw new Exception("SST-1 span/timing/palette oracle differs");}
    /// <summary>Programs two real ICS PLLs: A=50,113,000 Hz, B=25,056,500 Hz, then selects the requested graphics source.</summary>
    private static void GraphicsClock(VoodooGraphicsTests.Rig r,bool b)
    {
        r.Write(0x22c,0x40a);r.Write(0x22c,0x51a);r.Write(0x22c,0x506);
        r.Write(0x22c,0x40b);r.Write(0x22c,0x51a);r.Write(0x22c,0x526);
        r.Write(0x22c,0x40e);r.Write(0x22c,b?0x510u:0x500);
    }
    /// <summary>Registers geometry and elapsed-work proofs without changing CPU clocks or taking a host timing dependency.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        foreach(bool clockB in new[]{false,true})foreach(bool noVideo in new[]{false,true})test($"Voodoo covered-pixel deadline follows selected graphics PLL {clockB} and ignores scanout {noVideo}",()=>
        {
            var r=new VoodooGraphicsTests.Rig();GraphicsClock(r,clockB);VoodooGraphicsTests.Triangle(r);r.Write(0x110,0x200);
            if(noVideo)r.Write(0x20c,0);r.Write(0x80,0);
            Check(r.Card.CompletionNanoseconds-r.Clock.Now==(clockB?21791u:10896u)); // 496 independently covered pixels + 50 setup clocks.
            r.Finish();Check(r.Memory.ReadDword(Base+0x15c)==496);
            r.Write(0x118,8);r.Write(0x11c,8);r.Write(0x110,0x201);r.Write(0x80,0);
            Check(r.Card.CompletionNanoseconds-r.Clock.Now==(clockB?4550u:2275u));
        });
        test("Voodoo clear timing counts enabled planes once at two pixels per selected graphics clock",()=>
        {
            var r=new VoodooGraphicsTests.Rig();GraphicsClock(r,false);r.Write(0x118,3u<<16|6);r.Write(0x11c,5u<<16|12);
            r.Write(0x110,0x600);r.Write(0x124,0);Check(r.Card.CompletionNanoseconds-r.Clock.Now==1218);
        });
        foreach(bool clip in new[]{false,true})test($"Voodoo exact integer spans match an independent per-pixel oracle across signed/subpixel vertices and winding clip {clip}",()=>
        {
            var r=new VoodooGraphicsTests.Rig();var random=new Random(0x3df058);
            for(int trial=0;trial<48;trial++)
            {
                r.Write(0x118,96);r.Write(0x11c,96);r.Write(0x110,0x200);r.Write(0x148,0);r.Write(0x124,0);r.Finish();
                r.Write(0x120,1);r.Finish();VoodooGraphicsTests.Triangle(r);
                int[] v=Enumerable.Range(0,6).Select(_=>random.Next(-256,1280)).ToArray();
                if(trial==0)v=[0,0,512,0,0,512];if(trial==1)v=[0,0,0,512,512,0];if(trial==2)v=[8,8,520,8,8,520];if(trial==3)v=[0,0,512,512,1024,1024];
                for(int i=0;i<6;i++)r.Write(8+i*4,unchecked((uint)v[i]));
                r.Write(0x118,clip?4u<<16|57:96);r.Write(0x11c,clip?3u<<16|44:96);r.Write(0x110,clip?0x201u:0x200);r.Write(0x80,0);r.Finish();
                long area=(long)(v[2]-v[0])*(v[5]-v[1])-(long)(v[3]-v[1])*(v[4]-v[0]);
                if(area<0){(v[2],v[4])=(v[4],v[2]);(v[3],v[5])=(v[5],v[3]);}int count=0;
                for(int y=0;y<96;y++)for(int x=0;x<96;x++)
                {
                    bool inside=area!=0&&(!clip||x>=4&&x<57&&y>=3&&y<44);
                    for(int edge=0;edge<3;edge++)
                    {
                        int a=edge*2,b=(edge+1)%3*2;
                        long cross=((long)x*16+8-v[a])*(v[b+1]-v[a+1])-((long)y*16+8-v[a+1])*(v[b]-v[a]);
                        inside&=cross<0||cross==0&&(v[b+1]<v[a+1]||v[b+1]==v[a+1]&&v[b]>v[a]);
                    }
                    Check(r.Memory.ReadWord(Lfb+(uint)(y*2048+x*2))==(inside?0xf800:0));if(inside)count++;
                }
                Check(r.Memory.ReadDword(Base+0x15c)==count);
            }
        });
        foreach(int mutation in new[]{0,1,2})test($"Voodoo rejects corrupt authoritative palette or queued alias before adoption {mutation}",()=>
        {
            var r=new VoodooGraphicsTests.Rig();var block=r.Card.SaveState();var fields=new Dictionary<string,JsonElement>(block.Fields);
            if(mutation==0)fields["_texturePalette"]=JsonSerializer.SerializeToElement(new uint[255]);
            if(mutation==1){var colors=new uint[256];colors[137]=0x1000000;fields["_texturePalette"]=JsonSerializer.SerializeToElement(colors);}
            if(mutation==2){r.Write(0x124,0);r.Write(0x334,0x8000ff00);block=r.Card.SaveState();fields=new(block.Fields);var commands=fields["_commands"].Deserialize<uint[][]>()!;commands[1][1]&=0x7fffffff;fields["_commands"]=JsonSerializer.SerializeToElement(commands);}
            bool rejected=false;try{VoodooGraphics.ValidateSavedState(block with {Fields=fields});}catch(InvalidDataException){rejected=true;}Check(rejected);
        });
    }
}
