// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

public sealed partial class VgaAdapter
{
    // DB014-B section18 word-register slots; the last slot retains a partial multifunction MMIO write.
    private const int EY=0, EX=1, EDY=2, EDX=3, EE=4, EW=5, EH=6, ET=7, EL=8, EB=9, ER=10,
        EP=11, EM=12, EM2=13, ES=14, EC=15, ECmd=16, EShort=17, EY2=18, EX2=19, EDY2=20, EDX2=21,
        EE2=22, EW2=23, EPY=24, EPX=25, EDepth=26, EPitch=27, ELastMult=31;
    /// <summary>Canonical original engine word latches, including the current multifunction read selector.</summary>
    private ushort[] _trioEngineWords = new ushort[32];
    /// <summary>Canonical background/foreground/write-mask/read-mask/compare32-bit register values.</summary>
    private uint[] _trioEngineColors = new uint[5];
    /// <summary>Parameters captured by the actual active command, separate from subsequent programming of the next command.</summary>
    private ushort[] _trioEngineActiveWords = new ushort[32];
    /// <summary>Color and mask parameters captured by the actual active command.</summary>
    private uint[] _trioEngineActiveColors = new uint[5];
    /// <summary>Whether a supported drawing command still waits for actual CPU pixel data.</summary>
    private bool _trioEngineActive;
    /// <summary>Whether an explicitly unsupported command prevents a false successful idle result until engine reset.</summary>
    private bool _trioEngineBlocked;
    /// <summary>Number of actual pixels consumed by the incomplete CPU command.</summary>
    private int _trioEnginePixelIndex;
    /// <summary>Actual little-endian bytes collected for the next command-selected transfer group.</summary>
    private uint _trioEngineInputData;
    /// <summary>Actual partial through-plane pixel, including half of a32-bit pixel received through a16-bit bus.</summary>
    private uint _trioEnginePixelData;
    /// <summary>Number of actual bytes already collected in the incomplete transfer group.</summary>
    private byte _trioEngineInputBytes;
    /// <summary>Number of bytes already collected for the incomplete through-plane pixel.</summary>
    private byte _trioEnginePixelBytes;
    /// <summary>Canonical word address whose first MMIO byte has been read.</summary>
    private ushort _trioEngineReadAddress;
    /// <summary>Actual word sampled for a split byte-bus read, preserving multifunction/RSF read effects exactly once.</summary>
    private ushort _trioEngineReadLatch;
    /// <summary>Whether the second byte of the sampled MMIO word remains available.</summary>
    private bool _trioEngineReadValid;

    /// <summary>Whether actual CPU pixels remain outstanding; synchronous completed operations are already visible in shared VRAM.</summary>
    public bool TrioEngineWaiting => _trioEngineActive;
    /// <summary>Last blocked command for explicit unsupported-path diagnostics, without reporting successful execution.</summary>
    public ushort? TrioEngineUnsupportedCommand => _trioEngineBlocked ? _trioEngineWords[ECmd] : null;
    /// <summary>Enables only original enhanced-register memory mappings; no high-linear framebuffer bytes are intercepted.</summary>
    private bool TrioEngineMmio => IsTrio64 && TrioAdvancedAccessible && ((_crtc[0x53]&0x10)!=0 || (_trioAdvanced&0x20)!=0);
    /// <summary>Returns the original command pixel size; planar4-bit engine execution remains explicitly unsupported.</summary>
    private int TrioEnginePixelSize => (_crtc[0x50] & 0x30) switch { 0 => (_trioAdvanced & 4)==0 ? 1 : 0, 0x10=>2, 0x30=>4, _=>0 };
    /// <summary>Uses the graphics-command screen width, independently from the CRTC display pitch.</summary>
    private int TrioEnginePitch => (((_crtc[0x50]>>6)&3)|((_crtc[0x50]&1)<<2)) switch
    {0=>(_crtc[0x31]&2)!=0?2048:1024,1=>640,2=>800,3=>1280,4=>1152,6=>1600,_=>0};
    /// <summary>Clears the14 owned engine states without modifying display registers, VRAM, host time or IRQ sources.</summary>
    private void ResetTrioEngine()
    {
        Array.Clear(_trioEngineWords);Array.Clear(_trioEngineColors);ClearTrioActive();
        _trioEngineBlocked=false;ClearTrioRead();
    }
    /// <summary>Discards only the sampled split-read word.</summary>
    private void ClearTrioRead(){_trioEngineReadValid=false;_trioEngineReadAddress=_trioEngineReadLatch=0;}
    /// <summary>Ends or aborts the CPU command without undoing already written physical VRAM bytes.</summary>
    private void ClearTrioActive()
    {
        _trioEngineActive=false;_trioEnginePixelIndex=0;_trioEngineInputData=_trioEnginePixelData=0;
        _trioEngineInputBytes=_trioEnginePixelBytes=0;Array.Clear(_trioEngineActiveWords);Array.Clear(_trioEngineActiveColors);
    }
    /// <summary>Maps one original word port to the canonical register file.</summary>
    private static int TrioWordSlot(ushort port)=>port switch
    {0x82e8=>EY,0x86e8=>EX,0x8ae8=>EDY,0x8ee8=>EDX,0x92e8=>EE,0x96e8=>EW,0x9ae8=>ECmd,0x9ee8=>EShort,
     0xb6e8=>29,0xbae8=>30,0x82ea=>EY2,0x86ea=>EX2,0x8aea=>EDY2,0x8eea=>EDX2,0x92ea=>EE2,0x96ea=>EW2,
     0xeae8=>EPY,0xeaea=>EPX,0x42e8=>EC,_=>-1};
    /// <summary>Maps only the five original32-bit color/mask register bases.</summary>
    private static int TrioColorSlot(ushort port)=>(port&0xfffc) switch
    {0xa2e8=>0,0xa6e8=>1,0xaae8=>2,0xaee8=>3,0xb2e8=>4,_=>-1};
    /// <summary>Defines actual writable word bits; power-on-undefined registers use the board's explicit zero policy.</summary>
    private static ushort TrioEngineMask(int slot)=>slot switch
    {EY or EX or EW or EH or ET or EL or EB or ER or EY2 or EX2 or EW2 or EPY or EPX=>0x0fff,
     EDY or EDX or EE or EDY2 or EDX2 or EE2=>0x3fff,EP=>0xc0,EM=>0x3ff,EM2=>0x77,ES=>15,EC=>0xc000,
     ECmd or EShort or ELastMult=>0xffff,29 or 30=>0x6f,_=>0};
    /// <summary>Returns the synchronous empty FIFO and actual incomplete-command busy bits, without simulating bus timing.</summary>
    private ushort TrioEngineStatus=>(ushort)(0x400|(_trioEngineActive||_trioEngineBlocked?0x200:0));
    /// <summary>Tests the complete fixed enhanced port window, excluding normal VGA and setup ports.</summary>
    internal static bool IsTrioEnginePort(ushort port,int width)=>S3Trio64.EnginePortWindows.Any(w=>port>=w.Start&&port+width<=w.Start+w.Length);
    /// <summary>Reads actual engine ports after PCI/wake gates, respecting original MMIO-only selection.</summary>
    internal uint ReadTrioEnginePort(ushort port,int width)
    {
        uint floating=width==4?uint.MaxValue:(1u<<(width*8))-1;
        if(!IsTrio64||!TrioAdvancedAccessible||TrioEngineMmio&&(_seq[9]&0x80)!=0)return floating;
        int color=TrioColorSlot(port);
        if(width==4&&color>=0&&(port&3)==0)return (_trioEngineWords[EM]&0x200)!=0?_trioEngineColors[color]:floating;
        if(width==2&&(port&1)==0){ClearTrioRead();return ReadTrioEngineWord(port,false);}
        uint value=0;for(int i=0;i<width;i++)value|=(uint)ReadTrioEngineByte((ushort)(port+i),false)<<(8*i);return value;
    }
    /// <summary>Writes actual engine ports; a CPU transfer consumes bytes only through its real pixel-data endpoint.</summary>
    internal void WriteTrioEnginePort(ushort port,uint value,int width)
    {
        if(!IsTrio64||!TrioAdvancedAccessible||TrioEngineMmio&&(_seq[9]&0x80)!=0)return;ClearTrioRead();
        if(port is >=0xe2e8 and <=0xe2eb){for(int i=0;i<width;i++)FeedTrioEngineByte((byte)(value>>(8*i)));return;}
        int color=TrioColorSlot(port);
        if(width==4&&color>=0&&(port&3)==0){if((_trioEngineWords[EM]&0x200)!=0)_trioEngineColors[color]=value;return;}
        if(width==2&&(port&1)==0){WriteTrioEngineWord(port,(ushort)value,false);return;}
        for(int i=0;i<width;i++)WriteTrioEngineByte((ushort)(port+i),(byte)(value>>(8*i)),false);
    }
    /// <summary>Reads one original word, applying multifunction selector and32-bit-word selection side effects once.</summary>
    private ushort ReadTrioEngineWord(ushort port,bool mmio)
    {
        if(port==0x9ae8)return TrioEngineStatus;
        if(port==0x42e8)return (ushort)((_trioAdvanced&4)==0?0x80:0);
        if(port==0xbee8)
        {
            int index=_trioEngineWords[ES];_trioEngineWords[ES]=(ushort)((index+1)&15);
            return index switch {0=>_trioEngineWords[EH],1=>_trioEngineWords[ET],2=>_trioEngineWords[EL],3=>_trioEngineWords[EB],4=>_trioEngineWords[ER],5=>_trioEngineWords[EP],6=>_trioEngineWords[EM],7=>(ushort)(TrioEngineStatus&0x1fff),8=>(ushort)((_trioAdvanced&4)==0?0x80:0),10=>_trioEngineWords[EM2],_=>0xffff};
        }
        int color=TrioColorSlot(port);
        if(color>=0)
        {
            if(!mmio&&(_trioEngineWords[EM]&0x200)!=0)return 0xffff;
            int shift=mmio?(port&2)*8:TrioEnginePixelSize==4&&(_trioEngineWords[EM]&0x10)!=0?16:0;
            ushort value=(ushort)(_trioEngineColors[color]>>shift);
            if(!mmio&&TrioEnginePixelSize==4)_trioEngineWords[EM]^=0x10;return value;
        }
        int slot=TrioWordSlot(port);return slot is >=0 and not ECmd and not EShort?_trioEngineWords[slot]:(ushort)0xffff;
    }
    /// <summary>Decodes a complete multifunction or drawing-register write and executes only a complete command.</summary>
    private void WriteTrioEngineWord(ushort port,ushort value,bool mmio)
    {
        if(port==0x42e8)
        {
            if((value&0xc000)==0x8000)ResetTrioEngine();
            _trioEngineWords[EC]=(ushort)(value&0xc000);return;
        }
        if(port==0xbee8)
        {
            _trioEngineWords[ELastMult]=value;
            int slot=(value>>12) switch {0=>EH,1=>ET,2=>EL,3=>EB,4=>ER,10=>EP,13=>EM2,14=>EM,15=>ES,_=>-1};
            if(slot>=0)_trioEngineWords[slot]=(ushort)(value&TrioEngineMask(slot));return;
        }
        int color=TrioColorSlot(port);
        if(color>=0)
        {
            if(!mmio&&(_trioEngineWords[EM]&0x200)!=0)return;
            int shift=mmio?(port&2)*8:TrioEnginePixelSize==4&&(_trioEngineWords[EM]&0x10)!=0?16:0;
            _trioEngineColors[color]=(_trioEngineColors[color]&~(0xffffu<<shift))|((uint)value<<shift);
            if(!mmio&&TrioEnginePixelSize==4)_trioEngineWords[EM]^=0x10;return;
        }
        int target=TrioWordSlot(port);if(target<0)return;_trioEngineWords[target]=(ushort)(value&TrioEngineMask(target));
        if(target==ECmd)StartTrioEngine(value);else if(target==EShort)BlockTrioEngine(value);
    }
    /// <summary>Samples a low/high register byte while preserving one word's actual read side effects.</summary>
    private byte ReadTrioEngineByte(ushort port,bool mmio)
    {
        ushort address=(ushort)(port&0xfffe);
        if((port&1)==0||!_trioEngineReadValid||_trioEngineReadAddress!=address)
        {_trioEngineReadAddress=address;_trioEngineReadLatch=ReadTrioEngineWord(address,mmio);_trioEngineReadValid=true;}
        byte value=(byte)(_trioEngineReadLatch>>((port&1)*8));if((port&1)!=0)ClearTrioRead();return value;
    }
    /// <summary>Collects a byte-bus word write without firing CMD or multifunction effects before its high byte exists.</summary>
    private void WriteTrioEngineByte(ushort port,byte value,bool mmio)
    {
        ushort address=(ushort)(port&0xfffe);int color=TrioColorSlot(port);
        if(color>=0&&mmio)
        {int shift=(port&3)*8;_trioEngineColors[color]=(_trioEngineColors[color]&~(255u<<shift))|((uint)value<<shift);return;}
        int slot=address==0xbee8?ELastMult:TrioWordSlot(address);
        if(slot<0)
        {
            if(color>=0&&(_trioEngineWords[EM]&0x200)==0)
            {int shift=(port&1)*8+(TrioEnginePixelSize==4&&(_trioEngineWords[EM]&0x10)!=0?16:0);_trioEngineColors[color]=(_trioEngineColors[color]&~(255u<<shift))|((uint)value<<shift);}
            return;
        }
        ushort previous=_trioEngineWords[slot];ushort next=(ushort)((port&1)==0?(previous&0xff00)|value:(previous&255)|(value<<8));
        if((port&1)!=0)WriteTrioEngineWord(address,next,mmio);
        else _trioEngineWords[slot]=(ushort)(next&(slot is ECmd or ELastMult?0xffff:TrioEngineMask(slot)));
    }
    /// <summary>Maps original standard and packed MMIO byte addresses to the same real register file.</summary>
    private static bool TrioPackedPort(uint offset,out ushort port)
    {
        uint word=offset&~1u;port=word switch
        {0x8100=>0x82e8,0x8102=>0x86e8,0x8104=>0x82ea,0x8106=>0x86ea,0x8108=>0x8ae8,0x810a=>0x8ee8,
         0x810c=>0x8aea,0x810e=>0x8eea,0x8110=>0x92e8,0x8112=>0x92ea,0x8118=>0x9ae8,0x811a=>0x9aea,0x811c=>0x9ee8,
         0x8120=>0xa2e8,0x8122=>0xa2ea,0x8124=>0xa6e8,0x8126=>0xa6ea,0x8128=>0xaae8,0x812a=>0xaaea,
         0x812c=>0xaee8,0x812e=>0xaeea,0x8130=>0xb2e8,0x8132=>0xb2ea,0x8134=>0xb6e8,0x8136=>0xbae8,
         0x8168=>0xeae8,0x816a=>0xeaea,0x814a=>0x96e8,0x814c=>0x96ea,_=>0};
        if(port==0)return false;port+=(ushort)(offset&1);return true;
    }
    /// <summary>Consumes a fixed-aperture MMIO read before VGA/LFB suppression, keeping the documented packed aliases write-only.</summary>
    internal bool TryReadTrioEngineMmio(uint offset,out byte value)
    {
        value=255;if(!TrioEngineMmio||offset>=0x10000)return false;
        if(offset<0x8000||offset is >=0x8100 and <0x8170)return true;
        ushort port=(ushort)offset;if(IsTrioEnginePort(port,1))value=ReadTrioEngineByte(port,true);return true;
    }
    /// <summary>Writes real A0000 pixel transfers or original register aliases without redirecting high-linear VRAM accesses.</summary>
    internal bool TryWriteTrioEngineMmio(uint offset,byte value)
    {
        if(!TrioEngineMmio||offset>=0x10000)return false;ClearTrioRead();
        // DB014-B table13-1 also maps the write-only PIX_TRANS registers at AE2E8..AE2EB.
        if(offset<0x8000||offset is >=0xe2e8 and <=0xe2eb){FeedTrioEngineByte(value);return true;}
        if(offset is >=0x8138 and <=0x8149)
        {
            int slot=(offset&~1u) switch {0x8138=>ET,0x813a=>EL,0x813c=>EB,0x813e=>ER,0x8140=>EP,0x8142=>EM2,0x8144=>EM,0x8146=>ES,0x8148=>EH,_=>-1};
            if(slot>=0){int shift=(int)(offset&1)*8;_trioEngineWords[slot]=(ushort)((_trioEngineWords[slot]&~(255<<shift))|(value<<shift));_trioEngineWords[slot]&=TrioEngineMask(slot);}return true;
        }
        if(TrioPackedPort(offset,out ushort packed)){WriteTrioEngineByte(packed,value,true);return true;}
        ushort port=(ushort)offset;if(IsTrioEnginePort(port,1))WriteTrioEngineByte(port,value,true);return true;
    }
    /// <summary>Blocks an unsupported command instead of publishing an invented successful idle response.</summary>
    private void BlockTrioEngine(ushort command){ClearTrioActive();_trioEngineWords[ECmd]=command;_trioEngineBlocked=true;}
    /// <summary>Returns the primary command type with bit11 as its fourth high-order opcode bit.</summary>
    private static int TrioCommandType(ushort command)=>(command>>13)|((command>>8)&8);
    /// <summary>Returns the CPU transfer group width; value3 belongs only to an unimplemented Trio32 encoding.</summary>
    private static int TrioGroupBytes(ushort command)=>((command>>9)&3) switch {0=>1,1=>2,2=>4,_=>0};
    /// <summary>Determines whether actual CPU data supplies monochrome mix bits rather than complete pixel values.</summary>
    private static bool TrioMono(ushort[] words)=>(words[EP]&0xc0)==0x80;
    /// <summary>Requires a coherent supported CPU image/line form before accepting its first input byte.</summary>
    private static bool TrioCpuForm(ushort[] words)
    {
        int kind=TrioCommandType(words[ECmd]),group=TrioGroupBytes(words[ECmd]);
        if(kind is not (1 or 2)||group==0)return false;
        if(TrioMono(words))return (words[ECmd]&2)!=0&&(words[29]&0x60)!=0x40&&(words[30]&0x60)!=0x40;
        return (words[EP]&0xc0)==0&&(words[30]&0x60)==0x40&&!(words[EDepth]>1&&group==1);
    }
    /// <summary>Captures command parameters, executes real synchronous drawing, or retains an actual incomplete CPU transfer.</summary>
    private void StartTrioEngine(ushort command)
    {
        int kind=TrioCommandType(command);
        if(kind==0)return;
        if(_trioEngineActive||_trioEngineBlocked||(_trioEngineWords[EC]&0xc000)==0x8000||!TrioEnhanced||TrioEnginePixelSize==0||TrioEnginePitch==0)
        {BlockTrioEngine(command);return;}
        _trioEngineWords.CopyTo(_trioEngineActiveWords,0);_trioEngineColors.CopyTo(_trioEngineActiveColors,0);
        _trioEngineActiveWords[EDepth]=(ushort)TrioEnginePixelSize;_trioEngineActiveWords[EPitch]=(ushort)TrioEnginePitch;
        if((command&0x100)!=0)
        {
            if(!TrioCpuForm(_trioEngineActiveWords)){BlockTrioEngine(command);return;}
            _trioEngineActive=true;return;
        }
        if(kind is not (1 or 2 or 6 or 7)||(_trioEngineWords[EP]&0xc0) is 0x40 or 0x80||(_trioEngineWords[30]&0x60)==0x40)
        {BlockTrioEngine(command);return;}
        if(kind==1)
        {
            int count=_trioEngineActiveWords[EW]+1;
            for(int i=0;i<count;i++)
            {if((command&4)==0||i!=count-1)TrioEnginePixel(_trioEngineActiveWords[EX],_trioEngineActiveWords[EY],_trioEngineActiveWords[EX],_trioEngineActiveWords[EY],0,true);AdvanceTrioLine();}
        }
        else
        {
            int width=_trioEngineActiveWords[EW]+1,height=_trioEngineActiveWords[EH]+1;
            int dx=(command&0x20)!=0?1:-1,dy=(command&0x80)!=0?1:-1;
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            {
                int sx=(_trioEngineActiveWords[EX]+x*dx)&4095,sy=(_trioEngineActiveWords[EY]+y*dy)&4095;
                int tx=kind==2?sx:(_trioEngineActiveWords[EDX]+x*dx)&4095,ty=kind==2?sy:(_trioEngineActiveWords[EDY]+y*dy)&4095;
                if(kind==7){sx=(_trioEngineActiveWords[EX]+(tx&7))&4095;sy=(_trioEngineActiveWords[EY]+(ty&7))&4095;}
                TrioEnginePixel(tx,ty,sx,sy,0,true);
            }
            _trioEngineActiveWords[EY]=(ushort)((_trioEngineActiveWords[EY]+dy*height)&4095);
        }
        _trioEngineWords[EX]=_trioEngineActiveWords[EX];_trioEngineWords[EY]=_trioEngineActiveWords[EY];ClearTrioActive();
    }
    /// <summary>Advances exactly one axial/radial line position using the original signed14-bit error/step values.</summary>
    private void AdvanceTrioLine()
    {
        var w=_trioEngineActiveWords;int cmd=w[ECmd],x=w[EX],y=w[EY];
        if((cmd&8)!=0)
        {int direction=(cmd>>5)&7;x+=direction switch{0 or 1 or 7=>1,3 or 4 or 5=>-1,_=>0};y+=direction switch{1 or 2 or 3=>-1,5 or 6 or 7=>1,_=>0};}
        else
        {
            int dx=(cmd&0x20)!=0?1:-1,dy=(cmd&0x80)!=0?1:-1,error=TrioSigned14(w[EE]);
            if((cmd&0x40)!=0)y+=dy;else x+=dx;
            if(error>=0){if((cmd&0x40)!=0)x+=dx;else y+=dy;error+=TrioSigned14(w[EDX]);}else error+=TrioSigned14(w[EDY]);
            w[EE]=(ushort)(error&0x3fff);
        }
        w[EX]=(ushort)(x&4095);w[EY]=(ushort)(y&4095);
    }
    /// <summary>Interprets the original two's-complement14-bit line parameter.</summary>
    private static int TrioSigned14(ushort value)=>(value&0x2000)!=0?(value&0x3fff)-0x4000:value&0x3fff;
    /// <summary>Returns a masked logical pixel from an explicitly selected source/destination MiB region.</summary>
    private int TrioEngineAddress(int x,int y,bool source)
    {
        var w=_trioEngineActiveWords;int extended=source?(w[EM2]>>4)&7:w[EM2]&7;
        int basis=extended!=0?extended:source?(w[EM]>>2)&3:w[EM]&3;
        return basis*0x100000+((y&4095)*w[EPitch]+(x&4095))*w[EDepth];
    }
    /// <summary>Reads the exact shared little-endian8/16/32-bit pixel, with the board's explicit unpopulated-DRAM response.</summary>
    private uint ReadTrioEnginePixel(int address)
    {uint value=0;for(int i=0;i<_trioEngineActiveWords[EDepth];i++)value|=(uint)ReadTrioRaw(address+i)<<(8*i);return value;}
    /// <summary>Specifies all16 primary logical mixes before applying compare, clipping and the physical write mask.</summary>
    private static uint TrioMix(uint source,uint current,int operation)=>operation switch
    {0=>~current,1=>0,2=>uint.MaxValue,3=>current,4=>~source,5=>current^source,6=>~(current^source),7=>source,
     8=>~current|~source,9=>current|~source,10=>~current|source,11=>current|source,12=>current&source,
     13=>~current&source,14=>current&~source,_=>~current&~source};
    /// <summary>Executes one real graphics-engine read/mix/mask/store; it never substitutes a host framebuffer.</summary>
    private void TrioEnginePixel(int x,int y,int sx,int sy,uint cpu,bool mono)
    {
        var w=_trioEngineActiveWords;var colors=_trioEngineActiveColors;if((w[ECmd]&0x10)==0)return;
        bool inside=x>=w[EL]&&x<=w[ER]&&y>=w[ET]&&y<=w[EB];if(inside==((w[EM]&0x20)!=0))return;
        int size=w[EDepth],address=TrioEngineAddress(x,y,false);uint limit=size==4?uint.MaxValue:(1u<<(size*8))-1;
        uint bitmap=ReadTrioEnginePixel(TrioEngineAddress(sx,sy,true)),readMask=colors[3]&limit;
        bool foreground=(w[EP]&0xc0) switch{0=>true,0x80=>mono,0xc0=>(bitmap&readMask)==readMask,_=>false};
        int mix=w[foreground?30:29];uint source=((mix>>5)&3) switch{0=>colors[0],1=>colors[1],2=>cpu,_=>bitmap&readMask};
        uint current=ReadTrioEnginePixel(address),result=TrioMix(source,current,mix&15)&limit;
        if((w[EM]&0x100)!=0){bool equal=result==(colors[4]&limit);if(equal!=((w[EM]&0x80)!=0))return;}
        uint mask=colors[2]&limit;result=(current&~mask)|(result&mask);
        for(int i=0;i<size;i++)WriteTrioRaw(address+i,(byte)(result>>(8*i)));
    }
    /// <summary>Own lower-WORD MMIO compatibility for a final <=16-bit CPU-mono BUS32/SWP1 rectangle row; rejected offers are effect-free.</summary>
    internal bool TryWriteTrioEnginePixelWord(uint offset,ushort value)
    {
        if(!TrioEngineMmio||(offset&3)!=0||!(offset<0x8000||offset==0xe2e8)||!_trioEngineActive||_trioEngineBlocked||
            _trioEngineInputBytes!=0||_trioEngineInputData!=0||_trioEnginePixelBytes!=0||_trioEnginePixelData!=0)return false;
        var words=_trioEngineActiveWords;ushort command=words[ECmd];
        if(TrioCommandType(command)!=2||TrioGroupBytes(command)!=4||(command&0x1102)!=0x1102||!TrioMono(words))return false;
        int remaining=words[EW]+1-_trioEnginePixelIndex%(words[EW]+1);if(remaining is <1 or >16)return false;
        // The original native driver emits a separate lower-lane WORD trailer. Full DWORD and byte-group padding stay unchanged.
        ClearTrioRead();
        for(int b=0;b<2&&_trioEngineActive;b++)
        {
            byte input=(byte)(value>>(8*b));
            for(int bit=7;bit>=0&&_trioEngineActive;bit--)if(TransferTrioPixel(0,(input&(1<<bit))!=0))return true;
        }
        return true;
    }
    /// <summary>Accepts one actual pixel-data byte and preserves incomplete command-selected transfer groups.</summary>
    private void FeedTrioEngineByte(byte value)
    {
        if(!_trioEngineActive||_trioEngineBlocked)return;int bytes=TrioGroupBytes(_trioEngineActiveWords[ECmd]);
        _trioEngineInputData|=(uint)value<<(_trioEngineInputBytes*8);_trioEngineInputBytes++;
        if(_trioEngineInputBytes<bytes)return;
        uint group=_trioEngineInputData;_trioEngineInputData=0;_trioEngineInputBytes=0;
        bool lowFirst=(_trioEngineActiveWords[ECmd]&0x1000)!=0,mono=TrioMono(_trioEngineActiveWords);
        for(int b=0;b<bytes&&_trioEngineActive;b++)
        {
            byte input=(byte)(group>>(8*(lowFirst?b:bytes-1-b)));
            if(mono){for(int bit=7;bit>=0&&_trioEngineActive;bit--)if(TransferTrioPixel(0,(input&(1<<bit))!=0))return;}
            else
            {
                _trioEnginePixelData|=(uint)input<<(_trioEnginePixelBytes*8);_trioEnginePixelBytes++;
                if(_trioEnginePixelBytes==_trioEngineActiveWords[EDepth])
                {uint pixel=_trioEnginePixelData;_trioEnginePixelData=0;_trioEnginePixelBytes=0;if(TransferTrioPixel(pixel,true))return;}
            }
        }
    }
    /// <summary>Commits one supplied image/monochrome pixel and discards only documented transfer padding at a row end.</summary>
    private bool TransferTrioPixel(uint pixel,bool mono)
    {
        var w=_trioEngineActiveWords;int command=w[ECmd],kind=TrioCommandType((ushort)command),width=w[EW]+1,height=kind==1?1:w[EH]+1;
        int dx=(command&0x20)!=0?1:-1,dy=(command&0x80)!=0?1:-1;
        int x=kind==1?w[EX]:(w[EX]+(_trioEnginePixelIndex%width)*dx)&4095;
        int y=kind==1?w[EY]:(w[EY]+(_trioEnginePixelIndex/width)*dy)&4095;
        if(kind!=1||(command&4)==0||_trioEnginePixelIndex!=width-1)TrioEnginePixel(x,y,x,y,pixel,mono);
        if(kind==1)AdvanceTrioLine();_trioEnginePixelIndex++;
        bool rowEnd=_trioEnginePixelIndex%width==0;
        if(kind==1){_trioEngineWords[EX]=w[EX];_trioEngineWords[EY]=w[EY];}
        else{_trioEngineWords[EX]=(ushort)((w[EX]+(_trioEnginePixelIndex%width)*dx)&4095);_trioEngineWords[EY]=(ushort)((w[EY]+(_trioEnginePixelIndex/width)*dy)&4095);}
        if(_trioEnginePixelIndex==width*height)ClearTrioActive();return rowEnd;
    }
    /// <summary>Reads one mandatory explicit engine-state field without consuming a runtime reader or opening media.</summary>
    private static T TrioEngineField<T>(StateBlock state,string name)
    {
        if(!state.Fields.TryGetValue(name,out var value))throw new InvalidDataException("Missing Trio64 engine state: "+name);
        try{T result=value.Deserialize<T>(StateValues.Json)!;if(result is null)throw new InvalidDataException("Null Trio64 engine state: "+name);return result;}
        catch(JsonException error){throw new InvalidDataException("Invalid Trio64 engine state: "+name,error);}
    }
    /// <summary>Checks array dimensions, writable bits and incomplete CPU/MMIO transfer bounds before any media is opened.</summary>
    public static void ValidateTrio64EngineState(StateBlock state,bool isTrio64)
    {
        var words=TrioEngineField<ushort[]>(state,"_trioEngineWords");var colors=TrioEngineField<uint[]>(state,"_trioEngineColors");
        var activeWords=TrioEngineField<ushort[]>(state,"_trioEngineActiveWords");var activeColors=TrioEngineField<uint[]>(state,"_trioEngineActiveColors");
        bool active=TrioEngineField<bool>(state,"_trioEngineActive"),blocked=TrioEngineField<bool>(state,"_trioEngineBlocked");
        int index=TrioEngineField<int>(state,"_trioEnginePixelIndex");uint input=TrioEngineField<uint>(state,"_trioEngineInputData"),pixel=TrioEngineField<uint>(state,"_trioEnginePixelData");
        byte inputBytes=TrioEngineField<byte>(state,"_trioEngineInputBytes"),pixelBytes=TrioEngineField<byte>(state,"_trioEnginePixelBytes");
        ushort readAddress=TrioEngineField<ushort>(state,"_trioEngineReadAddress"),readLatch=TrioEngineField<ushort>(state,"_trioEngineReadLatch");bool readValid=TrioEngineField<bool>(state,"_trioEngineReadValid");
        if(words.Length!=32||activeWords.Length!=32||colors.Length!=5||activeColors.Length!=5)throw new InvalidDataException("Invalid Trio64 engine array shape.");
        for(int i=0;i<32;i++)
        {
            if((words[i]&~TrioEngineMask(i))!=0)throw new InvalidDataException("Invalid Trio64 engine word bits.");
            if(i is not (EDepth or EPitch)&&(activeWords[i]&~TrioEngineMask(i))!=0)throw new InvalidDataException("Invalid active Trio64 engine word bits.");
        }
        if(!isTrio64&&!IsTrio64EngineResetState(state))throw new InvalidDataException("A non-Trio adapter has foreign engine state.");
        if(readValid){if((readAddress&1)!=0||!IsTrioEnginePort(readAddress,2))throw new InvalidDataException("Invalid Trio64 split-read address.");}
        else if(readAddress!=0||readLatch!=0)throw new InvalidDataException("Inactive Trio64 split read has data.");
        if(!active)
        {
            if(index!=0||input!=0||pixel!=0||inputBytes!=0||pixelBytes!=0||activeWords.Any(x=>x!=0)||activeColors.Any(x=>x!=0))throw new InvalidDataException("Inactive Trio64 command has transfer data.");
            return;
        }
        int depth=activeWords[EDepth],pitch=activeWords[EPitch],group=TrioGroupBytes(activeWords[ECmd]);
        int height=TrioCommandType(activeWords[ECmd])==1?1:activeWords[EH]+1,total=(activeWords[EW]+1)*height;
        if(blocked||depth is not (1 or 2 or 4)||pitch is not (640 or 800 or 1024 or 1152 or 1280 or 1600 or 2048)||
            (activeWords[ECmd]&0x100)==0||!TrioCpuForm(activeWords)||index<0||index>=total||inputBytes>=group||pixelBytes>=depth||
            (TrioMono(activeWords)&&(pixelBytes!=0||pixel!=0))||!TrioEngineDataFits(input,inputBytes)||!TrioEngineDataFits(pixel,pixelBytes))
            throw new InvalidDataException("Invalid Trio64 CPU transfer phase.");
        int column=index%(activeWords[EW]+1);
        if(TrioMono(activeWords)?column%(group*8)!=0:(column*depth+pixelBytes)%group!=0)
            throw new InvalidDataException("Unreachable Trio64 transfer-group boundary.");
    }
    /// <summary>Requires no unreceived high bytes in an incomplete transfer value.</summary>
    private static bool TrioEngineDataFits(uint value,int bytes)=>bytes>=4||value>>(bytes*8)==0;
    /// <summary>Checks the board's complete explicit zero engine image for non-Trio adapters and held PCI reset.</summary>
    public static bool IsTrio64EngineResetState(StateBlock state)
    {
        return TrioEngineField<ushort[]>(state,"_trioEngineWords").All(x=>x==0)&&TrioEngineField<uint[]>(state,"_trioEngineColors").All(x=>x==0)&&
            TrioEngineField<ushort[]>(state,"_trioEngineActiveWords").All(x=>x==0)&&TrioEngineField<uint[]>(state,"_trioEngineActiveColors").All(x=>x==0)&&
            !TrioEngineField<bool>(state,"_trioEngineActive")&&!TrioEngineField<bool>(state,"_trioEngineBlocked")&&TrioEngineField<int>(state,"_trioEnginePixelIndex")==0&&
            TrioEngineField<uint>(state,"_trioEngineInputData")==0&&TrioEngineField<uint>(state,"_trioEnginePixelData")==0&&TrioEngineField<byte>(state,"_trioEngineInputBytes")==0&&
            TrioEngineField<byte>(state,"_trioEnginePixelBytes")==0&&TrioEngineField<ushort>(state,"_trioEngineReadAddress")==0&&TrioEngineField<ushort>(state,"_trioEngineReadLatch")==0&&
            !TrioEngineField<bool>(state,"_trioEngineReadValid");
    }
}

public sealed partial class S3Trio64
{
    /// <summary>Constructor-fixed original enhanced I/O resources, shared by runtime registration and exact bus-topology checks.</summary>
    public static IReadOnlyList<(ushort Start,int Length)> EnginePortWindows {get;}=Array.AsReadOnly(new (ushort,int)[]
    {(0x42e8,2),(0x82e8,4),(0x86e8,4),(0x8ae8,4),(0x8ee8,4),(0x92e8,4),(0x96e8,4),(0x9ae8,4),(0x9ee8,2),
     (0xa2e8,4),(0xa6e8,4),(0xaae8,4),(0xaee8,4),(0xb2e8,4),(0xb6e8,2),(0xbae8,2),(0xbee8,2),(0xe2e8,4),(0xeae8,4)});
}
