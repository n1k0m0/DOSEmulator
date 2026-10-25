using DOSEmulator.Core;

internal static class MemoryBusFastPathTests
{
    private static void Check(bool ok,string detail="Memory byte-bus compatibility") {if(!ok)throw new Exception(detail);}
    private sealed class Device : IMemoryDevice
    {
        public readonly List<string> Events=[];private readonly Dictionary<uint,byte> _written=[];private int _reads;
        public byte ReadByte(uint offset) {Events.Add("R"+offset);return _written.TryGetValue(offset,out byte v)?v:(byte)(offset*31+(uint)++_reads);}
        public void WriteByte(uint offset,byte value) {Events.Add($"W{offset}:{value}");_written[offset]=value;}
    }
    // Literal byte bus: no page cache, range shortcut, span or wide load/store.
    private sealed class ByteModel(int bytes)
    {
        public readonly byte[] Ram=new byte[bytes];public bool A20;
        public readonly List<(uint Start,uint Length,Device Device)> Maps=[];
        public byte Read(uint at)
        {
            if(!A20)at&=~0x100000u;
            foreach(var e in Maps)if(at>=e.Start&&(ulong)at<(ulong)e.Start+e.Length)return e.Device.ReadByte(at-e.Start);
            return at<Ram.Length?Ram[at]:(byte)255;
        }
        public void Write(uint at,byte value)
        {
            if(!A20)at&=~0x100000u;
            foreach(var e in Maps)if(at>=e.Start&&(ulong)at<(ulong)e.Start+e.Length){e.Device.WriteByte(at-e.Start,value);return;}
            if(at<Ram.Length)Ram[at]=value;
        }
    }
    public static void AddTo(Action<string,Action> test)
    {
        foreach(int width in new[]{2,4})foreach(bool a20 in new[]{false,true})foreach(int bytes in new[]{1048579,4*1048576})
            test($"RAM wide path matches ordered byte bus width={width}, A20={a20}, bytes={bytes}",()=>
            {
                var bus=new MemoryBus(bytes){A20Enabled=a20};var reference=new ByteModel(bytes){A20=a20};var devices=new List<(Device Actual,Device Expected)>();
                foreach(var(start,length) in new(uint,uint)[]{(0x2ffd,6),(0x6fff,1),(0x8ffe,9),(0xfffe,2),(0x10fffe,4),(0x20fffd,5),(0xfffffffe,2)})
                {
                    var x=new Device();var y=new Device();bus.Map(start,length,x,"probe");reference.Maps.Add((start,length,y));devices.Add((x,y));
                }
                var addresses=new List<uint>();
                foreach(uint boundary in new uint[]{0,0x1000,0x2ffd,0x3000,0x3003,0x6fff,0x7000,0x8ffe,0x9000,0x9007,0xfffe,0x10000,
                    0xfffff,0x100000,0x10fffe,0x110002,0x1fffff,0x200000,0x20fffd,0x210002,0x2fffff,0x300000,(uint)bytes,uint.MaxValue})
                    for(int d=-4;d<=4;d++)addresses.Add(unchecked(boundary+(uint)d));
                foreach(uint address in addresses)
                {
                    uint expected=0;for(int j=0;j<width;j++)expected|=(uint)reference.Read(unchecked(address+(uint)j))<<(8*j);
                    uint actual=width==2?bus.ReadWord(address):bus.ReadDword(address);Check(actual==expected,$"Read {address:X8}: {actual:X8} vs {expected:X8}");
                    uint value=unchecked(address*0x9e3779b9+0x89abcdef);
                    for(int j=0;j<width;j++)reference.Write(unchecked(address+(uint)j),(byte)(value>>(8*j)));
                    if(width==2)bus.WriteWord(address,(ushort)value);else bus.WriteDword(address,value);
                    foreach(var pair in devices)Check(pair.Actual.Events.SequenceEqual(pair.Expected.Events),"MMIO byte order/count changed");
                }
                Check(bus.Ram.AsSpan().SequenceEqual(reference.Ram),"RAM side effects differ");
            });
        test("late partial-page mapping invalidates a previously direct RAM path",()=>
        {
            var m=new MemoryBus(1048576);m.WriteDword(0x4000,0x12345678);Check(m.ReadDword(0x4000)==0x12345678);
            var d=new Device();m.Map(0x4001,1,d,"late partial");m.WriteDword(0x4000,0xaabbccdd);Check(m.ReadDword(0x4000)==0xaabbccdd);
            Check(d.Events.SequenceEqual(new[]{"W0:204","R0"}) && m.Ram[0x4001]==0x56);
        });
        test("mapping conflict cannot hide RAM or mutate the page cache",()=>
        {
            var m=new MemoryBus(1048576);m.Map(0x4fff,1,new Device(),"first");bool failed=false;
            try{m.Map(0x4fff,8194,new Device(),"bad");}catch(InvalidOperationException){failed=true;}
            m.WriteDword(0x6000,0x87654321);Check(failed && m.ReadDword(0x6000)==0x87654321 && m.Ram[0x6000]==0x21);
        });
        test("wide MMIO access observes a mapping added by an earlier byte read",()=>
        {
            var m=new MemoryBus(1048576);m.Ram[0x10000]=0x10;m.Ram[0x10002]=0x12;
            m.Map(0x10001,1,new CallbackDevice(()=>{m.Map(0x10003,1,new RomDevice([0xcc]),"late");return 0xaa;}),"trigger");
            Check(m.ReadDword(0x10000)==0xcc12aa10);
        });
    }
    private sealed class CallbackDevice(Func<byte> read):IMemoryDevice
    {public byte ReadByte(uint offset)=>read();public void WriteByte(uint offset,byte value){}}
}
