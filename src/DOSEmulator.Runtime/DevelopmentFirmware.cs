using System.Text;

namespace DOSEmulator.Runtime;

/// <summary>Original, reproducibly generated x86 machine code. No host-side BIOS interrupt interception.</summary>
public static class DevelopmentFirmware
{
    public static byte[] CreateBios()
    {
        var a = new CodeBuilder(0);
        a.Emit(0xfa, 0xfc, 0x31, 0xc0, 0x8e, 0xd8, 0x8e, 0xd0); // CLI; CLD; XOR AX,AX; DS=SS=0
        a.Mov(4, 0x7c00); a.Mov(0, 0xb800); a.Emit(0x8e, 0xc0); // SP, ES
        a.Emit(0x31, 0xff); a.Mov(1, 2000); a.Mov(0, 0x0720); a.Emit(0xf3, 0xab); // clear text cells
        a.Mov(0, 0xf000); a.Emit(0x8e, 0xd8); // DS points to ROM strings
        a.MovLabel(6, "banner"); a.Emit(0x31, 0xff); a.Call("print");
        a.Emit(0xb0, 0x10, 0xe6, 0x80); // POST progress code
        a.Mov(2, 0x1f6); a.Emit(0xb0, 0xe0, 0xee); // LBA master
        a.Mov(2, 0x1f2); a.Emit(0xb0, 1, 0xee, 0x42); // sector count = 1, next port
        a.Emit(0x30, 0xc0, 0xee, 0x42, 0xee, 0x42, 0xee); // LBA bytes = 0
        a.Mov(2, 0x1f7); a.Emit(0xb0, 0x20, 0xee); // READ SECTORS
        a.Mov(1, 0xffff);
        a.Label("poll"); a.Emit(0xec, 0x84, 0xc0); a.Jcc(0x74, "failed"); // no device
        a.Emit(0xa8, 0x80); a.Jcc(0x75, "retry");
        a.Emit(0xa8, 1); a.Jcc(0x75, "failed");
        a.Emit(0xa8, 8); a.Jcc(0x75, "ready");
        a.Label("retry"); a.Emit(0x49); a.Jcc(0x75, "poll"); a.Jump("failed");
        a.Label("ready"); a.Emit(0x31, 0xc0, 0x8e, 0xc0); // ES=0
        a.Mov(7, 0x7c00); a.Mov(1, 256); a.Mov(2, 0x1f0); a.Emit(0xf3, 0x6d); // REP INSW
        a.Emit(0x26, 0x81, 0x3e, 0xfe, 0x7d, 0x55, 0xaa); a.Jcc(0x75, "invalid");
        a.Emit(0xb0, 0x20, 0xe6, 0x80, 0x31, 0xc0, 0x8e, 0xd8, 0xb2, 0x80);
        a.Emit(0xea, 0x00, 0x7c, 0x00, 0x00); // guest executes its own sector at 0000:7C00
        a.Label("failed"); a.MovLabel(6, "diskError"); a.Jump("error");
        a.Label("invalid"); a.MovLabel(6, "signatureError");
        a.Label("error"); a.Mov(0, 0xb800); a.Emit(0x8e, 0xc0); a.Mov(7, 160 * 6); a.Call("print");
        a.Label("stop"); a.Emit(0xfa, 0xf4); a.Jump("stop");
        EmitPrint(a);
        a.Label("banner"); a.String("DOSEmulator - Virtual 486 AT / Development BIOS");
        a.Label("diskError"); a.String("BOOT ERROR: no readable primary IDE master (read-only LBA28).");
        a.Label("signatureError"); a.String("BOOT ERROR: sector has no 55 AA signature.");
        byte[] code = a.Finish();
        if (code.Length >= 0xfff0) throw new InvalidOperationException("Development BIOS is too large.");
        byte[] rom = Enumerable.Repeat((byte)0xff, 65536).ToArray();
        code.CopyTo(rom, 0);
        new byte[] { 0xea, 0, 0, 0, 0xf0 }.CopyTo(rom, 0xfff0); // reset-alias far jump
        return rom;
    }

    public static byte[] CreateDemoDisk()
    {
        var a = new CodeBuilder(0x7c00);
        a.Emit(0xfa, 0xfc, 0x31, 0xc0, 0x8e, 0xd8, 0x8e, 0xd0); a.Mov(4, 0x7c00);
        a.Mov(0, 0xb800); a.Emit(0x8e, 0xc0);
        void Line(int row, string label) { a.Mov(7, (ushort)(row * 160)); a.MovLabel(6, label); a.Call("print"); }
        Line(4, "title"); Line(6, "path"); Line(8, "limit"); Line(10, "next"); Line(13, "controls");
        a.Emit(0xc7, 0x06, 0x00, 0x05, 0xef, 0xbe); // observable boot-sector marker
        a.Emit(0xb0, 0x30, 0xe6, 0x80);
        a.Label("stop"); a.Emit(0xf4); a.Jump("stop");
        EmitPrint(a);
        a.Label("title"); a.String("486 BOOT PATH VERIFIED");
        a.Label("path"); a.String("RESET ROM -> x86 firmware -> ATA PIO -> guest boot sector");
        a.Label("limit"); a.String("Own test sector. FreeDOS real-mode boot is tested separately.");
        a.Label("next"); a.String("Next: interrupts, timers, keyboard, BIOS services and DOS boot.");
        a.Label("controls"); a.String("CTRL+ALT+F10 MENU   CTRL+ALT+F11 FULLSCREEN");
        byte[] code = a.Finish();
        if (code.Length > 510) throw new InvalidOperationException($"Demo boot sector is too large: {code.Length} bytes.");
        byte[] disk = new byte[128 * 512];
        code.CopyTo(disk, 0); disk[510] = 0x55; disk[511] = 0xaa;
        return disk;
    }

    private static void EmitPrint(CodeBuilder a)
    {
        a.Label("print"); a.Emit(0xac, 0x84, 0xc0); a.Jcc(0x74, "printDone"); // LODSB; TEST; JZ
        a.Emit(0xe6, 0xe9, 0xb4, 0x0f, 0xab); a.Jump("print"); // debug output and STOSW
        a.Label("printDone"); a.Emit(0xb0, 10, 0xe6, 0xe9, 0xc3);
    }

    private sealed class CodeBuilder(ushort origin)
    {
        private readonly List<byte> _bytes = [];
        private readonly Dictionary<string, int> _labels = [];
        private readonly List<(int Offset, string Label, int Width, bool Relative)> _patches = [];
        public void Emit(params byte[] bytes) => _bytes.AddRange(bytes);
        public void Label(string label) => _labels.Add(label, _bytes.Count);
        public void String(string text) { _bytes.AddRange(Encoding.ASCII.GetBytes(text)); _bytes.Add(0); }
        public void Mov(int register, ushort value) => Emit((byte)(0xb8 + register), (byte)value, (byte)(value >> 8));
        public void MovLabel(int register, string label) { Emit((byte)(0xb8 + register)); Patch(label, 2, false); }
        public void Call(string label) { Emit(0xe8); Patch(label, 2, true); }
        public void Jump(string label) { Emit(0xe9); Patch(label, 2, true); }
        public void Jcc(byte opcode, string label) { Emit(opcode); Patch(label, 1, true); }
        private void Patch(string label, int width, bool relative)
        {
            _patches.Add((_bytes.Count, label, width, relative));
            for (int i = 0; i < width; i++) _bytes.Add(0);
        }
        public byte[] Finish()
        {
            foreach (var p in _patches)
            {
                int value = p.Relative ? _labels[p.Label] - (p.Offset + p.Width) : origin + _labels[p.Label];
                if (p.Width == 1 && value is < -128 or > 127) throw new InvalidOperationException($"Short branch to {p.Label} out of range.");
                for (int i = 0; i < p.Width; i++) _bytes[p.Offset + i] = (byte)(value >> (i * 8));
            }
            return _bytes.ToArray();
        }
    }
}
