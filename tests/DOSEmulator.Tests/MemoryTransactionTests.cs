// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

/// <summary>Width-preserving dynamic MMIO must never bypass fixed maps, A20 lane order or byte-only fallback devices.</summary>
internal static class MemoryTransactionTests
{
    /// <summary>Rejects any bus transaction shape or lane sequence discrepancy.</summary>
    private static void Check(bool value) { if(!value) throw new Exception("Dynamic MMIO transaction boundary differs"); }
    private sealed class Peer : IMemoryTransactionDecoder
    {
        public readonly List<uint> Bytes=[];
        public int Reads,Writes,Width;
        public uint Value;
        /// <summary>Claims only the isolated high-memory probe window, away from installed RAM.</summary>
        public bool Decodes(uint address)=>address is >=0x200000 and <0x400000;
        /// <summary>Records every original byte fallback without a wide side effect.</summary>
        public byte ReadDecodedByte(uint address){Bytes.Add(address);return 0x5a;}
        /// <summary>Records every original byte fallback without publishing a complete command.</summary>
        public void WriteDecodedByte(uint address,byte value){Bytes.Add(address);}
        /// <summary>Returns an identifiable complete wide transaction result.</summary>
        public uint ReadDecoded(uint address,int width){Reads++;Width=width;return 0x12345678;}
        /// <summary>Records exactly one complete wide transaction and its little-endian value.</summary>
        public void WriteDecoded(uint address,uint value,int width){Writes++;Width=width;Value=value;}
    }
    /// <summary>Registers both widths and independent negative physical-boundary conditions.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        foreach(int width in new[]{2,4})
        {
            test($"Memory transaction complete dynamic width {width}",()=>
            {
                var memory=new MemoryBus(1048576);var peer=new Peer();memory.MapDynamic(peer);
                uint value=width==2?memory.ReadWord(0x200000):memory.ReadDword(0x200000);
                Check(value==(width==2?0x5678u:0x12345678u)&&peer.Reads==1&&peer.Bytes.Count==0&&peer.Width==width);
                if(width==2)memory.WriteWord(0x200000,0xaabb);else memory.WriteDword(0x200000,0xaabbccdd);
                Check(peer.Writes==1&&peer.Value==(width==2?0xaabbu:0xaabbccddu)&&peer.Bytes.Count==0);
            });
            test($"Memory transaction fixed priority boundary width {width}",()=>
            {
                var memory=new MemoryBus(1048576);var peer=new Peer();memory.MapDynamic(peer);memory.Map(0x200001,1,new OpenBusMemory(),"Own fixed byte");
                uint value=width==2?memory.ReadWord(0x200000):memory.ReadDword(0x200000);
                Check(value==(width==2?0xff5au:0x5a5aff5au)&&peer.Reads==0&&peer.Bytes.Count==width-1);
                peer.Bytes.Clear();if(width==2)memory.WriteWord(0x200000,0);else memory.WriteDword(0x200000,0);
                Check(peer.Writes==0&&peer.Bytes.Count==width-1);
            });
            test($"Memory transaction A20 folding boundary width {width}",()=>
            {
                var memory=new MemoryBus(1048576){A20Enabled=false};var peer=new Peer();memory.MapDynamic(peer);uint start=0x300000u-(uint)width/2;
                if(width==2)memory.WriteWord(start,0);else memory.WriteDword(start,0);
                Check(peer.Writes==0&&peer.Bytes.SequenceEqual(Enumerable.Range(0,width).Select(i=>(start+(uint)i)&~0x100000u)));
            });
            test($"Memory transaction decoder edge width {width}",()=>
            {
                var memory=new MemoryBus(1048576);var peer=new Peer();memory.MapDynamic(peer);uint start=0x400000u-(uint)width/2;
                _=width==2?memory.ReadWord(start):memory.ReadDword(start);Check(peer.Reads==0&&peer.Bytes.Count==width/2);
            });
        }
    }
}
