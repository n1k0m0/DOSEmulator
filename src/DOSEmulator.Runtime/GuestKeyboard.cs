namespace DOSEmulator.Runtime;

/// <summary>US set-1 typing for deterministic guest acceptance tests; bytes pass through KBC and IRQ1.</summary>
public static class GuestKeyboard
{
    private const string Unshifted = "\0\u001b1234567890-=\b\tqwertyuiop[]\r\0asdfghjkl;'`\0\\zxcvbnm,./\0*\0 ";
    private const string Shifted = "\0\u001b!@#$%^&*()_+\b\tQWERTYUIOP{}\r\0ASDFGHJKL:\"~\0|ZXCVBNM<>?\0*\0 ";
    /// <summary>Acceptance-test pacing for the reference BIOS. Avoid overflowing its type-ahead ring during slow disk I/O.</summary>
    public static bool TryInjectForBios(PcMachine machine, byte scanCode)
    {
        if (machine.Keyboard.OutputByteCount != 0) return false;
        ushort head = machine.Memory.ReadWord(0x41a), tail = machine.Memory.ReadWord(0x41c);
        if (head is < 0x1e or >= 0x3e || tail is < 0x1e or >= 0x3e || ((head | tail) & 1) != 0) return false;
        int queued = ((tail - head + 32) % 32) / 2;
        if (queued >= 8) return false;
        machine.Keyboard.InjectTranslatedSet1(scanCode); return true;
    }
    public static byte[] Encode(string text)
    {
        var bytes = new List<byte>();
        char previous = '\0';
        foreach (char raw in text)
        {
            if (raw == '\n' && previous == '\r') { previous = raw; continue; }
            char character = raw == '\n' ? '\r' : raw;
            int scan = Unshifted.IndexOf(character); bool shift = false;
            if (scan <= 0) { scan = Shifted.IndexOf(character); shift = true; }
            if (scan <= 0) throw new ArgumentException($"Character U+{(int)character:X4} cannot be typed with the test US layout.");
            if (shift) bytes.Add(0x2a);
            bytes.Add((byte)scan); bytes.Add((byte)(scan | 0x80));
            if (shift) bytes.Add(0xaa);
            previous = raw;
        }
        return bytes.ToArray();
    }
}
