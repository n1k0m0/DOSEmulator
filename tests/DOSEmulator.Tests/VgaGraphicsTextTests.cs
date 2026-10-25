using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

internal static class VgaGraphicsTextTests
{
    private static readonly uint[] Colors = [0xff000000,0xff0000aa,0xff00aa00,0xff00aaaa,0xffaa0000,0xffaa00aa,0xffaa5500,0xffaaaaaa,
        0xff555555,0xff5555ff,0xff55ff55,0xff55ffff,0xffff5555,0xffff55ff,0xffffff55,0xffffffff];
    private static void Check(bool value, string message = "Graphics text assertion failed") { if (!value) throw new Exception(message); }
    private static void Reg(VgaAdapter v, ushort port, int index, int value) => v.Write(port, (uint)(index | value << 8), 2);
    private static byte Get(VgaAdapter v, ushort port, int index) { v.Write(port, (uint)index, 1); return (byte)v.Read((ushort)(port+1), 1); }
    private static void Bios(PcMachine m, ushort ax)
    {
        m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.Eip=0x1000; m.Memory.WriteByte(0x1000, 0xf4);
        m.Cpu.SetRegister(Register.Ax, ax); m.Cpu.EnterRealModeInterrupt(0x10); m.RunSteps(3000000);
        Check(m.Fault is null && m.Cpu.Halted, m.Fault ?? "INT10 did not return");
    }
    private static PcMachine Boot(int mode)
    {
        var m=PcMachine.CreateDemo(); m.RunSteps(150000); Check(m.Fault is null && m.Cpu.Halted, m.Fault ?? "POST did not halt"); Bios(m,(ushort)mode); return m;
    }
    private static byte[] Font(int points)
    {
        byte[] font=new byte[256*points];
        for(int ch=0;ch<256;ch++) for(int row=1;row<points-1;row++)
            font[ch*points+row]=(byte)(row==1?ch:row==2?ch^0x5a:(ch << (row%7) | ch >> (8-row%7)));
        return font;
    }
    private static byte[] Load(PcMachine m,int mode,int points)
    {
        byte[] font=Font(points); font.CopyTo(m.Memory.Ram,0x23000);
        m.Cpu.SetSegment(Segment.Es,0x2000);m.Cpu.SetRegister(Register.Bp,0x3000);m.Cpu.SetRegister(Register.Bx,0);
        m.Cpu.SetRegister(Register.Cx,(uint)points);m.Cpu.SetRegister(Register.Dx,(uint)((mode==0x12?480:200)/points));Bios(m,0x1121);return font;
    }
    private static void Cursor(PcMachine m,int row,int column)
    { m.Cpu.SetRegister(Register.Bx,0);m.Cpu.SetRegister(Register.Dx,(uint)(row<<8|column));Bios(m,0x0200); }
    private static void Glyph(byte[] pixels,int width,byte[] font,int points,int ch,int row,int col,byte foreground,byte background=0,bool xor=false)
    {
        for(int y=0;y<points;y++)for(int x=0;x<8;x++)
        {
            int at=(row*points+y)*width+col*8+x;bool on=(font[ch*points+y]&(128>>x))!=0;
            if(xor){if(on)pixels[at]^=foreground;}else pixels[at]=on?foreground:background;
        }
    }
    private static void Frame(PcMachine m,byte[] expected)
    {
        var actual=m.Video.Capture(0);Check(actual.ArgbPixels.Length==expected.Length);
        for(int i=0;i<expected.Length;i++)
        {
            uint color=expected[i] switch {201=>0xffff0000,37=>0xff0000ff,_=>Colors[expected[i]]};
            Check(actual.ArgbPixels[i]==color,$"Pixel {i%actual.Width},{i/actual.Width}: {actual.ArgbPixels[i]:X8} != {color:X8}");
        }
    }
    private static void Dac(PcMachine m,int index,int r,int g,int b)
    {m.Cpu.SetRegister(Register.Bx,(uint)index);m.Cpu.SetRegister(Register.Dx,(uint)(r<<8));m.Cpu.SetRegister(Register.Cx,(uint)(g<<8|b));Bios(m,0x1010);}
    private static byte[] Seed(PcMachine m,int mode)
    {
        int width=mode==0x12?640:320,height=mode==0x12?480:200;byte[] pixels=new byte[width*height];
        if(mode==0x12)
        {
            Reg(m.Video,0x3ce,5,2);
            for(int y=0;y<height;y++)for(int x=0;x<80;x++)
            {byte color=(byte)((y*7+x)%16);m.Memory.WriteByte((uint)(0xa0000+y*80+x),color);pixels.AsSpan(y*width+x*8,8).Fill(color);}
            Reg(m.Video,0x3ce,5,0);
        }
        else for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {byte color=(byte)((x+y*3)%16);pixels[y*width+x]=color;m.Memory.WriteByte((uint)(0xa0000+y*width+x),color);}
        return pixels;
    }
    private static byte[] Registers(VgaAdapter v)
    {
        int si=(int)v.Read(0x3c4,1),gi=(int)v.Read(0x3ce,1),ci=(int)v.Read(0x3d4,1);
        byte[] result=[(byte)si,(byte)gi,(byte)ci,..Enumerable.Range(0,5).Select(i=>Get(v,0x3c4,i)),..Enumerable.Range(0,9).Select(i=>Get(v,0x3ce,i)),..Enumerable.Range(0,25).Select(i=>Get(v,0x3d4,i))];
        v.Write(0x3c4,(uint)si,1);v.Write(0x3ce,(uint)gi,1);v.Write(0x3d4,(uint)ci,1);return result;
    }
    private static byte[] Planes(PcMachine m)
    {
        byte[] registers=Registers(m.Video),result=new byte[4*65536];
        Reg(m.Video,0x3c4,4,6);Reg(m.Video,0x3ce,5,0);Reg(m.Video,0x3ce,6,5);
        for(int p=0;p<4;p++){Reg(m.Video,0x3ce,4,p);for(uint i=0;i<65536;i++)result[p*65536+(int)i]=m.Memory.ReadByte(0xa0000+i);}
        for(int i=0;i<5;i++)Reg(m.Video,0x3c4,i,registers[3+i]);
        for(int i=0;i<9;i++)Reg(m.Video,0x3ce,i,registers[8+i]);
        m.Video.Write(0x3c4,registers[0],1);m.Video.Write(0x3ce,registers[1],1);return result;
    }
    public static void AddTo(Action<string,Action> test)
    {
        test("Graphics INT1F font pointer changes independently of mode, geometry, INT43 and VGA contents",()=>
        {
            foreach(int mode in new[]{3,0x12,0x13})
            {
                using var m=Boot(mode);byte[] before=Planes(m),bda=m.Memory.Ram.AsSpan(0x449,64).ToArray();uint vector=m.Memory.ReadDword(0x10c);
                m.Cpu.SetSegment(Segment.Es,0x2345);m.Cpu.SetRegister(Register.Bp,0xabcdffff);Bios(m,0x1120);
                Check(m.Memory.ReadDword(0x7c)==0x2345ffff&&m.Memory.ReadDword(0x10c)==vector&&Planes(m).SequenceEqual(before)&&m.Memory.Ram.AsSpan(0x449,64).SequenceEqual(bda));
                m.Cpu.SetRegister(Register.Bx,0);Bios(m,0x1130);Check(m.Cpu.GetSegment(Segment.Es)==0x2345&&m.Cpu.GetRegister(Register.Bp)==0xabcdffff);
            }
        });
        foreach(var(mode,call,selector,rows,points,pointer) in new[]{(0x12,0x1122,1,14,14,0xa800),(0x12,0x1122,2,25,14,0xa800),(0x12,0x1123,3,43,8,0xa000),(0x12,0x1124,2,25,16,0xc000),(0x13,0x1122,1,14,14,0xa800),(0x13,0x1123,2,25,8,0xa000),(0x13,0x1124,0,12,16,0xc000)})
            test($"Graphics ROM font {call:X} selects {rows} rows of {points} points in mode {mode:X} without rewriting VRAM or CRTC",()=>
            {
                using var m=Boot(mode);byte[] before=Planes(m),registers=Registers(m.Video);ushort page=m.Memory.ReadWord(0x44c);int generation=m.Video.ModeGeneration;
                m.Cpu.SetRegister(Register.Bx,(uint)selector);m.Cpu.SetRegister(Register.Dx,(uint)rows);Bios(m,(ushort)call);
                Check(m.Memory.ReadDword(0x10c)==(0xf0000000u|(uint)pointer)&&m.Memory.ReadWord(0x485)==points&&m.Memory.ReadByte(0x484)==rows-1);
                Check(m.Memory.ReadWord(0x44c)==page&&m.Video.ModeGeneration==generation&&Registers(m.Video).SequenceEqual(registers)&&Planes(m).SequenceEqual(before));
                m.Cpu.SetRegister(Register.Bx,0x0100);Bios(m,0x1130);Check(m.Cpu.GetSegment(Segment.Es)==0xf000&&(m.Cpu.GetRegister(Register.Bp)&65535)==pointer&&m.Cpu.GetRegister(Register.Cx)==points&&(m.Cpu.GetRegister(Register.Dx)&255)==rows-1);
            });
        foreach(var(mode,points,rows) in new[]{(0x12,32,15),(0x13,1,200)})
            test($"User graphics font supports the bounded {points}-point/{rows}-row edge without changing hardware geometry",()=>
            {
                using var m=Boot(mode);byte[] before=Planes(m),regs=Registers(m.Video);Load(m,mode,points);
                Check(m.Memory.ReadDword(0x10c)==0x20003000&&m.Memory.ReadWord(0x485)==points&&m.Memory.ReadByte(0x484)==rows-1&&Planes(m).SequenceEqual(before)&&Registers(m.Video).SequenceEqual(regs));
            });
        test("Invalid graphics font height/row selectors/grids are atomic and graphics font selection is ignored in text mode",()=>
        {
            foreach(int mode in new[]{3,0x12,0x13})
            {
                using var m=Boot(mode);uint vector=m.Memory.ReadDword(0x10c);byte[] bda=m.Memory.Ram.AsSpan(0x449,64).ToArray(),planes=Planes(m);
                foreach(var(points,selector,rows)in new[]{(0,0,1),(33,0,1),(8,4,25),(8,0,0),(32,0,255)})
                {m.Cpu.SetRegister(Register.Cx,(uint)points);m.Cpu.SetRegister(Register.Bx,(uint)selector);m.Cpu.SetRegister(Register.Dx,(uint)rows);Bios(m,0x1121);Check(m.Memory.ReadDword(0x10c)==vector&&m.Memory.Ram.AsSpan(0x449,64).SequenceEqual(bda)&&Planes(m).SequenceEqual(planes));}
                if(mode==3){m.Cpu.SetRegister(Register.Bx,2);Bios(m,0x1124);Check(m.Memory.ReadDword(0x10c)==vector&&m.Memory.Ram.AsSpan(0x449,64).SequenceEqual(bda));}
            }
        });
        foreach(int mode in new[]{0x12,0x13})foreach(int points in new[]{8,14,16})
            test($"Mode {mode:X} draws/repeats a {points}-point guest glyph with correct colors and clips at the same-row boundary",()=>
            {
                using var m=Boot(mode);byte[] font=Load(m,mode,points);int width=m.Video.Width,height=m.Video.Height,cols=width/8;
                byte[] expected=new byte[width*height];Cursor(m,2,cols-2);
                byte fg=(byte)(mode==0x12?14:201),bg=(byte)(mode==0x12?0:37);if(mode==0x13){Dac(m,201,63,0,0);Dac(m,37,0,0,63);}
                m.Cpu.SetRegister(Register.Bx,(uint)(bg<<8|fg));m.Cpu.SetRegister(Register.Cx,65535);Bios(m,0x0941);
                Glyph(expected,width,font,points,65,2,cols-2,fg,bg);Glyph(expected,width,font,points,65,2,cols-1,fg,bg);Frame(m,expected);
                Check(m.Memory.ReadWord(0x450)==(2<<8|cols-2),"Repeated writes moved the cursor");
                Cursor(m,3,1);m.Cpu.SetRegister(Register.Bx,11);m.Cpu.SetRegister(Register.Cx,1);Bios(m,0x0a42);Glyph(expected,width,font,points,66,3,1,11);Frame(m,expected);
            });
        test("Planar graphics XOR changes foreground bits only and drawing twice restores every pixel",()=>
        {
            using var m=Boot(0x12);byte[] font=Load(m,0x12,8),expected=Seed(m,0x12),original=expected.ToArray();Cursor(m,5,3);
            m.Cpu.SetRegister(Register.Bx,0x85);m.Cpu.SetRegister(Register.Cx,1);Bios(m,0x0951);Glyph(expected,640,font,8,81,5,3,5,xor:true);Frame(m,expected);
            Bios(m,0x0951);Frame(m,original);Check(m.Memory.ReadWord(0x450)==0x0503);
        });
        foreach(var(mode,points)in new[]{(0x12,32),(0x13,1)})
            test($"Graphics {points}-point edge glyph draws and reads the final cell without escaping the framebuffer",()=>
            {
                using var m=Boot(mode);byte[] font=Load(m,mode,points);
                if(points==1){font[65]=255;m.Memory.WriteByte(0x23000+65,255);}
                int width=m.Video.Width,height=m.Video.Height,row=height/points-1,col=width/8-1;
                Cursor(m,row,col);m.Cpu.SetRegister(Register.Bx,14);m.Cpu.SetRegister(Register.Cx,1);Bios(m,0x0941);
                byte[] expected=new byte[width*height];Glyph(expected,width,font,points,65,row,col,14);Frame(m,expected);
                Bios(m,0x0800);Check(m.Cpu.GetRegister(Register.Ax)==(points==1?0u:65u),"Reader did not handle its maximum sample-buffer extent or an ambiguous uniform glyph");
                Check(m.Memory.ReadWord(0x450)==(row<<8|col));
            });
        foreach(int mode in new[]{0x12,0x13})foreach(bool down in new[]{false,true})foreach(int count in new[]{0,1,255})
            test($"Graphics mode {mode:X} rectangular {(down?"down":"up")} scroll {count} preserves outside pixels and offscreen VRAM",()=>
            {
                using var m=Boot(mode);Load(m,mode,8);byte[] original=Seed(m,mode),expected=original.ToArray(),before=Planes(m);int width=m.Video.Width;
                for(int y=24;y<56;y++)for(int x=16;x<64;x++)
                {int sy=y+(down?-count:count)*8;expected[y*width+x]=(byte)(count==0||sy<24||sy>=56?13:original[sy*width+x]);}
                m.Cpu.SetRegister(Register.Cx,0x0302);m.Cpu.SetRegister(Register.Dx,0x0607);m.Cpu.SetRegister(Register.Bx,0x0d00);Bios(m,(ushort)((down?0x0700:0x0600)|count));Frame(m,expected);
                byte[] after=Planes(m);int used=mode==0x12?38400:16000;
                for(int p=0;p<4;p++)Check(after.AsSpan(p*65536+used,65536-used).SequenceEqual(before.AsSpan(p*65536+used,65536-used)),"Scroll escaped the visible framebuffer");
            });
        foreach(int mode in new[]{0x12,0x13})
            test($"Graphics AH08 mode {mode:X} recognizes actual two-color guest glyph pixels and rejects a third color",()=>
            {
                using var m=Boot(mode);Load(m,mode,8);Cursor(m,2,3);m.Cpu.SetRegister(Register.Bx,(uint)(mode==0x12?11:37<<8|201));m.Cpu.SetRegister(Register.Cx,1);Bios(m,0x0941);
                m.Cpu.SetRegister(Register.Bx,0);Bios(m,0x0800);Check(m.Cpu.GetRegister(Register.Ax)==65,$"Read glyph {m.Cpu.GetRegister(Register.Ax)}");
                m.Cpu.SetRegister(Register.Cx,24);m.Cpu.SetRegister(Register.Dx,16);Bios(m,0x0c05);Bios(m,0x0800);Check(m.Cpu.GetRegister(Register.Ax)==0,"Reader used cached text after a direct pixel change");
            });
        test("Indexed AH08 reads a glyph written solely by guest-memory bytes without a character-write call",()=>
        {
            using var m=Boot(0x13);byte[] font=Load(m,0x13,8);Cursor(m,2,3);
            for(int y=0;y<8;y++)for(int x=0;x<8;x++)m.Memory.WriteByte((uint)(0xa0000+(16+y)*320+24+x),(byte)((font[66*8+y]&(128>>x))!=0?201:37));
            Bios(m,0x0800);Check(m.Cpu.GetRegister(Register.Ax)==66);
        });
        test("Graphics glyph source wraps bytewise at the font pointer's segment end",()=>
        {
            using var m=Boot(0x13);byte[] glyph=[0,0x18,0x3c,0x7e,255,0x66,0x42,0];
            for(int i=0;i<8;i++)m.Memory.WriteByte((uint)(0x20000+((0xfffc+i)&65535)),glyph[i]);
            m.Cpu.SetSegment(Segment.Es,0x2000);m.Cpu.SetRegister(Register.Bp,0xfffc);m.Cpu.SetRegister(Register.Cx,8);m.Cpu.SetRegister(Register.Bx,0);m.Cpu.SetRegister(Register.Dx,25);Bios(m,0x1121);
            Cursor(m,1,2);m.Cpu.SetRegister(Register.Bx,14);m.Cpu.SetRegister(Register.Cx,1);Bios(m,0x0900);
            byte[] expected=new byte[64000];Glyph(expected,320,glyph,8,0,1,2,14);Frame(m,expected);
        });
        foreach(int mode in new[]{0x12,0x13})foreach(int move in new[]{0,1})
            test($"Graphics AH13 mode {mode:X}, move={move}, draws controls from real source memory and retains its requested cursor behavior",()=>
            {
                using var m=Boot(mode);byte[] font=Load(m,mode,8);Cursor(m,6,7);new byte[]{65,13,10,66,8,67}.CopyTo(m.Memory.Ram,0x24000);
                m.Cpu.SetSegment(Segment.Es,0x2000);m.Cpu.SetRegister(Register.Bp,0x4000);m.Cpu.SetRegister(Register.Cx,6);m.Cpu.SetRegister(Register.Dx,0x0202);m.Cpu.SetRegister(Register.Bx,14);Bios(m,(ushort)(0x1300|move));
                byte[] expected=new byte[m.Video.Width*m.Video.Height];Glyph(expected,m.Video.Width,font,8,65,2,2,14);Glyph(expected,m.Video.Width,font,8,67,3,0,14);Frame(m,expected);
                Check(m.Memory.ReadWord(0x450)==(move==0?0x0607:0x0301)&&m.Cpu.GetRegister(Register.Bp)==0x4000&&m.Cpu.GetRegister(Register.Dx)==0x0202&&m.Cpu.GetSegment(Segment.Es)==0x2000);
            });
        foreach(int mode in new[]{0x12,0x13})
            test($"Graphics teletype mode {mode:X} wraps/scrolls actual pixels and ignores BH as a text-page request",()=>
            {
                using var m=Boot(mode);byte[] font=Load(m,mode,8),expected=Seed(m,mode);int width=m.Video.Width,height=m.Video.Height,rows=height/8;Cursor(m,rows-1,width/8-1);
                Glyph(expected,width,font,8,90,rows-1,width/8-1,14);Array.Copy(expected,width*8,expected,0,expected.Length-width*8);expected.AsSpan(expected.Length-width*8).Clear();
                m.Cpu.SetRegister(Register.Bx,0x070e);Bios(m,0x0e5a);Frame(m,expected);Check(m.Memory.ReadWord(0x450)==((rows-1)<<8));
            });
        test("Graphics BIOS operations restore hostile VGA register state/index latches and CPU flags/registers",()=>
        {
            foreach(int mode in new[]{0x12,0x13})
            {
                using var m=Boot(mode);Load(m,mode,8);Cursor(m,1,2);
                Reg(m.Video,0x3c4,2,5);Reg(m.Video,0x3c4,4,2);
                int[] gc=[7,11,12,0x1d,3,mode==0x12?0x1b:0x5b,0x0d,7,0x51];for(int i=0;i<gc.Length;i++)Reg(m.Video,0x3ce,i,gc[i]);
                m.Video.Write(0x3c4,1,1);m.Video.Write(0x3ce,7,1);m.Video.Write(0x3d4,4,1);byte[] regs=Registers(m.Video);
                m.Cpu.SetRegister(Register.Bx,0xabcd000e);m.Cpu.SetRegister(Register.Cx,0x98760001);m.Cpu.SetRegister(Register.Dx,0x54320102);m.Cpu.SetRegister(Register.Bp,0x43211234);
                m.Cpu.SetRegister(Register.Si,0x23454321);m.Cpu.SetRegister(Register.Di,0x45676543);m.Cpu.SetSegment(Segment.Ds,0x1234);m.Cpu.SetSegment(Segment.Es,0x2345);m.Cpu.Eflags=2|X86Cpu.Direction|X86Cpu.Carry;
                uint sp=m.Cpu.GetRegister(Register.Sp);Bios(m,0x0941);
                Check(Registers(m.Video).SequenceEqual(regs)&&m.Cpu.Eflags==(2|X86Cpu.Direction|X86Cpu.Carry)&&m.Cpu.GetRegister(Register.Sp)==sp);
                Check(m.Cpu.GetRegister(Register.Bx)==0xabcd000e&&m.Cpu.GetRegister(Register.Cx)==0x98760001&&m.Cpu.GetRegister(Register.Dx)==0x54320102&&m.Cpu.GetRegister(Register.Bp)==0x43211234);
                Check(m.Cpu.GetRegister(Register.Si)==0x23454321&&m.Cpu.GetRegister(Register.Di)==0x45676543&&m.Cpu.GetSegment(Segment.Ds)==0x1234&&m.Cpu.GetSegment(Segment.Es)==0x2345);
                m.Cpu.SetRegister(Register.Bx,0);Bios(m,0x0800);Check(m.Cpu.GetRegister(Register.Ax)==65&&Registers(m.Video).SequenceEqual(regs));
                m.Cpu.SetRegister(Register.Cx,0);m.Cpu.SetRegister(Register.Dx,0x0304);m.Cpu.SetRegister(Register.Bx,0x0e00);Bios(m,0x0601);Check(Registers(m.Video).SequenceEqual(regs));
            }
        });
        test("Invalid graphics rectangles/pages/string forms/counts leave all VRAM and cursor bytes unchanged",()=>
        {
            foreach(int mode in new[]{0x12,0x13})
            {
                using var m=Boot(mode);Load(m,mode,8);Seed(m,mode);byte[] before=Planes(m),bda=m.Memory.Ram.AsSpan(0x449,64).ToArray();
                foreach(var(ax,bx,cx,dx)in new[]{(0x0941,0,0,0),(0x0a41,0x0100,1,0),(0x1302,0,1,0),(0x1301,0x0100,1,0),(0x1301,0,0,0x0101),(0x0601,0,0x0401,0x0202),(0x0701,0,0x0104,0x0202),(0x0600,0,0,0xff00)})
                {m.Cpu.SetRegister(Register.Bx,(uint)bx);m.Cpu.SetRegister(Register.Cx,(uint)cx);m.Cpu.SetRegister(Register.Dx,(uint)dx);Bios(m,(ushort)ax);Check(Planes(m).SequenceEqual(before)&&m.Memory.Ram.AsSpan(0x449,64).SequenceEqual(bda),$"Invalid {mode:X}/{ax:X} mutated video state");}
            }
        });
    }
}
