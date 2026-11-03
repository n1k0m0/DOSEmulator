// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using DOSEmulator.Core;
using DOSEmulator.Whpx;

/// <summary>Runs the actual own compiled x86 guest DLL in WHPX; records real OUT payloads without loading it into the host.</summary>
internal static class NativeGlideGuestChecks
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const uint ImageBase = 0x100000;

    /// <summary>Rejects an actual compiled ABI or copied-byte discrepancy.</summary>
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    /// <summary>Maps and relocates only the supplied own PE32 image into ordinary probe RAM, leaving imported Windows APIs uncalled.</summary>
    private static uint[] LoadImage(byte[] file, byte[] ram)
    {
        int pe = BitConverter.ToInt32(file, 60), optional = pe + 24;
        Check(BitConverter.ToUInt16(file, optional) == 0x10b, "Expected own PE32 Glide guest DLL");
        uint oldBase = BitConverter.ToUInt32(file, optional + 28), size = BitConverter.ToUInt32(file, optional + 56);
        Check(size < 7 << 20, "Own probe image exceeds bounded RAM");
        int sections = BitConverter.ToUInt16(file, pe + 6), table = optional + BitConverter.ToUInt16(file, pe + 20);
        for (int i = 0; i < sections; i++)
        {
            int row = table + i * 40; uint address = BitConverter.ToUInt32(file, row + 12);
            int bytes = BitConverter.ToInt32(file, row + 16), raw = BitConverter.ToInt32(file, row + 20);
            file.AsSpan(raw, bytes).CopyTo(ram.AsSpan(checked((int)(ImageBase + address)), bytes));
        }
        uint reloc = BitConverter.ToUInt32(file, optional + 136), relocBytes = BitConverter.ToUInt32(file, optional + 140);
        for (uint at = ImageBase + reloc, end = at + relocBytes; at < end;)
        {
            uint page = BitConverter.ToUInt32(ram, (int)at), block = BitConverter.ToUInt32(ram, (int)at + 4);
            Check(block >= 8 && at + block <= end, "Invalid own PE relocation block");
            for (uint offset = 8; offset < block; offset += 2)
            {
                ushort entry = BitConverter.ToUInt16(ram, (int)(at + offset));
                if ((entry >> 12) == 0) continue;
                Check((entry >> 12) == 3, "Unexpected own PE relocation type");
                int target = checked((int)(ImageBase + page + (uint)(entry & 4095)));
                BinaryPrimitives.WriteUInt32LittleEndian(ram.AsSpan(target, 4), unchecked(BitConverter.ToUInt32(ram, target) + ImageBase - oldBase));
            }
            at += block;
        }
        uint export = ImageBase + BitConverter.ToUInt32(file, optional + 96);
        uint first = BitConverter.ToUInt32(ram, (int)export + 16), count = BitConverter.ToUInt32(ram, (int)export + 20);
        uint functions = ImageBase + BitConverter.ToUInt32(ram, (int)export + 28);
        var result = new uint[first + count];
        for (uint i = 0; i < count; i++) result[first + i] = ImageBase + BitConverter.ToUInt32(ram, (int)(functions + i * 4));
        return result;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong Triangle(ulong a, ulong b, ulong c);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong Swap(ulong interval);

    /// <summary>Executes actual stdcall exports, mutating all caller vertices before each later queue flush.</summary>
    public static void Run(string path, bool scalar)
    {
        int cases = 0;
        foreach (int count in new[] { 1, 87, 88, 170 })
        {
            var memory = new MemoryBus(8 << 20); uint[] exports = LoadImage(File.ReadAllBytes(path), memory.Ram);
            var code = new List<byte>(); var expected = new List<byte[]>();
            // Appends exactly the declared own native instruction bytes.
            void Add(params byte[] bytes) => code.AddRange(bytes);
            // Emits one own little-endian native operand or displacement.
            void Dword(uint word) => Add(BitConverter.GetBytes(word));
            // Calls the actual relocated own stdcall export with its original return cleanup.
            void Call(uint target) { Add(0xe8); Dword(unchecked(target - (0x1000u + (uint)code.Count + 4))); }
            Add(0xbc); Dword(0xe0000);
            for (int draw = 0; draw < count; draw++)
            {
                var record = new List<byte>();
                for (int vertex = 0; vertex < 3; vertex++) for (int word = 0; word < 15; word++)
                {
                    uint bits = (uint)(draw * 4096 + vertex * 256 + word); record.AddRange(BitConverter.GetBytes(bits));
                    Add(0xc7, 5); Dword((uint)(0x80000 + vertex * 4096 + word * 4)); Dword(bits);
                }
                expected.Add(record.ToArray());
                foreach (uint pointer in new uint[] { 0x82000, 0x81000, 0x80000 }) { Add(0x68); Dword(pointer); }
                Call(exports[107]);
                for (int vertex = 0; vertex < 3; vertex++)
                {
                    Add(0xbf); Dword((uint)(0x80000 + vertex * 4096)); Add(0xb9); Dword(15); Add(0xb8); Dword(0xcccccccc); Add(0xf3, 0xab);
                }
            }
            Add(0x6a, 0); Call(exports[16]); Add(0xf4); code.ToArray().CopyTo(memory.Ram, 0x1000);
            string? original = Environment.GetEnvironmentVariable("DOSE_GLIDE_HOST_DLL");
            WhpxMappedMachine engine;
            try
            {
                Environment.SetEnvironmentVariable("DOSE_GLIDE_HOST_DLL", Path.Combine(Environment.SystemDirectory, "kernel32.dll"));
                engine = new WhpxMappedMachine(memory, new(), new(), new(), WhpxCpuContract.IntelP54cB5Subset, 100_000_000);
            }
            finally { Environment.SetEnvironmentVariable("DOSE_GLIDE_HOST_DLL", original); }
            using var native = engine;
            object bridge = typeof(WhpxMappedMachine).GetField("_glidePassThrough", Hidden)!.GetValue(native)!;
            var functions = (nint[])bridge.GetType().GetField("_functions", Hidden)!.GetValue(bridge)!;
            var observed = new List<byte[]>(); int swaps = 0;
            Triangle triangle = (a, b, c) =>
            {
                byte[] vertices = new byte[180];
                Marshal.Copy((nint)a, vertices, 0, 60); Marshal.Copy((nint)b, vertices, 60, 60); Marshal.Copy((nint)c, vertices, 120, 60);
                observed.Add(vertices); return 0;
            };
            Swap swap = _ => { swaps++; return 0; };
            functions[107] = Marshal.GetFunctionPointerForDelegate(triangle); functions[16] = Marshal.GetFunctionPointerForDelegate(swap);
            native.SetEntry(0, 0x1000); var initial = native.CaptureCpuState();
            native.RestoreCpuState(initial with { Registers = initial.Registers.Select(r => r.Name switch
            {
                0x1c => r with { Low = 0x60000031 }, 19 => r with { Low = 0, High = 0x409b0008ffffffff },
                18 or 20 or 21 or 22 or 23 => r with { Low = 0, High = 0x40930010ffffffff }, _ => r
            }).ToArray() });
            native.RunUntilHalt(TimeSpan.FromSeconds(10), 10000);
            Check(observed.Count == count && swaps == 1 && native.GetRegister(4) == 0xe0000, "Compiled Glide stdcall or call count differs");
            for (int i = 0; i < count; i++) Check(observed[i].AsSpan().SequenceEqual(expected[i]), "Compiled guest lost caller-time vertex bytes or order");
            Check((long)bridge.GetType().GetField("_transportExits", Hidden)!.GetValue(bridge)! == (scalar ? count + 1 : (count + 86) / 87 + 1), "Compiled guest did not use the declared scalar/batch control"); GC.KeepAlive(triangle); GC.KeepAlive(swap); cases++;
        }
        Console.WriteLine($"PASS: own compiled Glide guest {cases} native stdcall/OUT controls, scalar={scalar}; 346 exact triangle records, caller reuse, queue capacity and swap flush; no host renderer/window/API imports executed.");
    }
}
