// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Hardware;

internal static class ReviewDeviceTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool c,string detail="review device invariant"){if(!c)throw new Exception(detail);}
    /// <summary>Registers the named review device regression cases with the common runner.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        test("PIC pending cache matches original scalar controller for 100000 line/mask/ICW/EOI/ACK operations",()=>
        {
            var fast=new Pic8259();var scalar=new ScalarPicReference();var random=new Random(813);
            for(int i=0;i<100000;i++)
            {
                switch(random.Next(5))
                {
                    case 0:case 1:{int irq=random.Next(16);bool high=random.Next(2)==1;fast.SetLine(irq,high);scalar.SetLine(irq,high);break;}
                    case 2:{ushort port=new ushort[]{0x20,0x21,0xa0,0xa1}[random.Next(4)];uint value=(uint)random.Next(256);fast.Write(port,value,1);scalar.Write(port,value,1);break;}
                    case 3:Check(fast.Acknowledge()==scalar.Acknowledge(),"PIC vector");break;
                    default:for(int n=0;n<4;n++)Check(fast.HasPending==scalar.HasPending);break;
                }
                Check(fast.HasPending==scalar.HasPending,"PIC pending after mutation");
                foreach(ushort port in new ushort[]{0x20,0x21,0xa0,0xa1})Check(fast.Read(port,1)==scalar.Read(port,1),"PIC readable state");
            }
        });
        test("VGA chain-4 shortcut matches original planar byte algorithm across every guard and plane mask",()=>
        {
            var v=new VgaAdapter();var planes=Enumerable.Range(0,4).Select(_=>new byte[65536]).ToArray();var latch=new byte[4];var random=new Random(919);
            // Writes an actual VGA sequencer index/data pair for the controlled device case.
            void Seq(int i,int value)=>v.Write(0x3c4,(uint)(i|value<<8),2);
            // Writes an actual VGA graphics-controller index/data pair for the controlled device case.
            void Gc(int i,int value)=>v.Write(0x3ce,(uint)(i|value<<8),2);
            Seq(2,15);Seq(4,4);Gc(1,0);Gc(3,0);Gc(5,0);Gc(6,5);Gc(8,255);for(uint i=0;i<65536;i++)v.WriteByte(i,0);
            foreach(int chain in new[]{0,8})foreach(int map in Enumerable.Range(0,16))foreach(int mode in Enumerable.Range(0,4))foreach(int rotate in new[]{0,1,7,8,16,24})foreach(int mask in new[]{0,1,15,240,255})foreach(int enable in new[]{0,1,8,15})
            {
                Seq(4,chain|4);Seq(2,map);Gc(5,mode);Gc(3,rotate);Gc(8,mask);Gc(1,enable);Gc(0,9);
                for(int n=0;n<2;n++)
                {
                    int offset=random.Next(65536),address=chain!=0?offset>>2:offset;byte value=(byte)random.Next(256);Gc(4,n);
                    for(int p=0;p<4;p++)latch[p]=planes[p][address];Check(v.ReadByte((uint)offset)==latch[chain!=0?offset&3:n],"VGA source/latch");
                    int allowed=map;if(chain!=0)allowed&=1<<(offset&3);
                    byte rotated=(byte)((value>>(rotate&7))|(value<<(8-(rotate&7))));int bitmask=mode==3?mask&rotated:mask;
                    for(int p=0;p<4;p++)if((allowed&(1<<p))!=0)
                    {
                        if(mode==1){planes[p][address]=latch[p];continue;}
                        int bits=mode==2?((value&(1<<p))!=0?255:0):mode==3?((9&(1<<p))!=0?255:0):(enable&(1<<p))!=0?((9&(1<<p))!=0?255:0):rotated;
                        bits=(rotate>>3&3) switch{1=>bits&latch[p],2=>bits|latch[p],3=>bits^latch[p],_=>bits};planes[p][address]=(byte)((bits&bitmask)|(latch[p]&~bitmask));
                    }
                    v.WriteByte((uint)offset,value);
                    for(int p=0;p<4;p++)
                    {
                        Gc(4,p);uint readOffset=(uint)(chain!=0?(address*4+p):address);Check(v.ReadByte(readOffset)==planes[p][address],"VGA plane result");
                    }
                }
            }
        });
        test("RAM span refuses internal A20 folds and mapped/partial pages",()=>
        {
            var m=new MemoryBus(8*1024*1024);m.A20Enabled=false;Check(!m.TryGetRamSpan(0,2*1024*1024+1,out _));Check(!m.TryGetRamSpan(0xfffff,2,out _));Check(m.TryGetRamSpan(0x100100,4,out var folded));folded[0]=77;Check(m.Ram[0x100]==77);
            m.A20Enabled=true;m.Map(0x30123,1,new RomDevice([1]),"partial");Check(!m.TryGetRamSpan(0x30000,1,out _));m.Map(0x40000,4096,new RomDevice(new byte[4096]),"full");Check(!m.TryGetRamSpan(0x40000,4,out _));Check(m.TryGetRamSpan(0x50000,4096,out _));m.A20Enabled=false;Check(!m.IsPlainRamByte(0x130123),"folded mapped pages require opaque-device checkpoints");
        });
    }
}





/// <summary>Development pair of edge-triggered 8259-compatible controllers, fixed priority and normal EOI.</summary>
internal sealed class ScalarPicReference : IIoPortDevice
{
    private sealed class Chip(byte vector)
    {
        public byte Vector = vector, Mask = 0xff, Irr, Isr, Lines;
        public int InitStep;
        public bool NeedsIcw4, Single, AutoEoi, ReadIsr;
        /// <summary>Selects the highest eligible request under the current mask and in-service priority.</summary>
        public int Pending()
        {
            int activePriority = 8;
            for (int i = 0; i < 8; i++) if ((Isr & (1 << i)) != 0) { activePriority = i; break; }
            for (int i = 0; i < activePriority; i++) if ((Irr & ~Mask & (1 << i)) != 0) return i;
            return -1;
        }
        /// <summary>Updates an external interrupt input, recognizing only the model's eligible edge/level transitions.</summary>
        public void SetLine(int irq, bool high)
        {
            byte bit = (byte)(1 << irq);
            if (high && (Lines & bit) == 0) Irr |= bit;
            Lines = high ? (byte)(Lines | bit) : (byte)(Lines & ~bit);
        }
        /// <summary>Selects and acknowledges an eligible interrupt, including cascade and spurious-vector behavior.</summary>
        public int Acknowledge()
        {
            int irq = Pending();
            if (irq < 0) return -1;
            Irr &= (byte)~(1 << irq);
            if (!AutoEoi) Isr |= (byte)(1 << irq);
            return Vector + irq;
        }
        /// <summary>Submits the requested operation through the component's normal command interface.</summary>
        public void Command(byte value)
        {
            if ((value & 0x10) != 0)
            {
                InitStep = 1; NeedsIcw4 = (value & 1) != 0; Single = (value & 2) != 0;
                Irr = Isr = Mask = Lines = 0; AutoEoi = false; ReadIsr = false; return;
            }
            if ((value & 0x18) == 8) { if ((value & 2) != 0) ReadIsr = (value & 1) != 0; return; }
            if ((value & 0x20) != 0)
            {
                if ((value & 0x40) != 0) Isr &= (byte)~(1 << (value & 7));
                else for (int i = 0; i < 8; i++) if ((Isr & (1 << i)) != 0) { Isr &= (byte)~(1 << i); break; }
            }
        }
        /// <summary>Processes the test PIC's explicit initialization/mask data byte.</summary>
        public void Data(byte value)
        {
            if (InitStep == 1) { Vector = (byte)(value & 0xf8); InitStep = Single ? NeedsIcw4 ? 3 : 0 : 2; }
            else if (InitStep == 2) InitStep = NeedsIcw4 ? 3 : 0;
            else if (InitStep == 3) { AutoEoi = (value & 2) != 0; InitStep = 0; }
            else Mask = value;
        }
    }
    private readonly Chip _master = new(8), _slave = new(0x70);
    /// <summary>Updates an external interrupt input, recognizing only the model's eligible edge/level transitions.</summary>
    public void SetLine(int irq, bool high)
    {
        if ((uint)irq >= 16) throw new ArgumentOutOfRangeException(nameof(irq));
        if (irq < 8) _master.SetLine(irq, high); else _slave.SetLine(irq - 8, high);
        RefreshCascade();
    }
    /// <summary>Updates the slave-to-master PIC cascade level after priority, mask or request changes.</summary>
    private void RefreshCascade() => _master.SetLine(2, _slave.Pending() >= 0);
    /// <summary>Whether the PIC has an unmasked request eligible under its current priority/service state.</summary>
    public bool HasPending { get { RefreshCascade(); return _master.Pending() >= 0; } }
    /// <summary>Selects and acknowledges an eligible interrupt, including cascade and spurious-vector behavior.</summary>
    public byte? Acknowledge()
    {
        RefreshCascade(); int irq = _master.Pending(); int vector = _master.Acknowledge();
        if (vector < 0) return null;
        if (irq == 2) { int slaveVector = _slave.Acknowledge(); RefreshCascade(); return (byte)(slaveVector < 0 ? _slave.Vector + 7 : slaveVector); }
        return (byte)vector;
    }
    /// <summary>Reads the fixture's actual result for comparison with independently specified expected data.</summary>
    public uint Read(ushort port, int width)
    {
        Chip chip = port >= 0xa0 ? _slave : _master;
        return (port & 1) != 0 ? chip.Mask : chip.ReadIsr ? chip.Isr : chip.Irr;
    }
    /// <summary>Writes explicit private-fixture input data without modifying original user media.</summary>
    public void Write(ushort port, uint value, int width)
    {
        if (width != 1) return;
        Chip chip = port >= 0xa0 ? _slave : _master;
        if ((port & 1) != 0) chip.Data((byte)value); else chip.Command((byte)value);
        RefreshCascade();
    }
}
