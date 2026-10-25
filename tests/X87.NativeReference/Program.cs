using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
    throw new PlatformNotSupportedException("The optional independent host-x87 oracle needs Windows x64.");
if (args.Length != 3) throw new ArgumentException("code.bin vectors.json result.json");
byte[] code = File.ReadAllBytes(args[0]);
using var vectors = JsonDocument.Parse(File.ReadAllText(args[1]));
IntPtr executable = Native.VirtualAlloc(IntPtr.Zero, (nuint)code.Length, 0x3000, 4);
if (executable == IntPtr.Zero) throw new Win32Exception();
try
{
    Marshal.Copy(code, 0, executable, code.Length);
    if (!Native.VirtualProtect(executable, (nuint)code.Length, 0x20, out _)) throw new Win32Exception();
    if (!Native.FlushInstructionCache(Native.GetCurrentProcess(), executable, (nuint)code.Length)) throw new Win32Exception();
    var run = Marshal.GetDelegateForFunctionPointer<Native.Run>(executable);
    var results = new List<object>();
    foreach (var v in vectors.RootElement.EnumerateArray())
    {
        var bytes = Enumerable.Repeat((byte)0xa5, 160).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, v.GetProperty("control").GetUInt16());
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), v.GetProperty("operation").GetUInt16());
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), v.GetProperty("kind").GetUInt16());
        foreach (var (key, at) in new[] { ("a", 8), ("b", 18) })
        {
            var raw = v.GetProperty(key);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at), Convert.ToUInt64(raw.GetProperty("significand").GetString(), 16));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 8), Convert.ToUInt16(raw.GetProperty("exponent").GetString(), 16));
        }
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), Convert.ToUInt64(v.GetProperty("bits").GetString(), 16));
        IntPtr block = Marshal.AllocHGlobal(bytes.Length);
        try { Marshal.Copy(bytes, 0, block, bytes.Length); run(block); Marshal.Copy(block, bytes, 0, bytes.Length); }
        finally { Marshal.FreeHGlobal(block); }
        var registers = Enumerable.Range(0, 8).Select(i => new
        {
            significand = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(68 + i * 10)).ToString("X16"),
            exponent = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(76 + i * 10)).ToString("X4")
        }).ToArray();
        results.Add(new { id = v.GetProperty("id").GetString(), control = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(40)),
            status = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(44)), tags = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(48)),
            registers, memory = Convert.ToHexString(bytes.AsSpan(148, 8)) });
    }
    File.WriteAllText(args[2], JsonSerializer.Serialize(new { architecture = "host-x87-x64", referenceOnly = true,
        processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"), runtime = RuntimeInformation.FrameworkDescription,
        nativeCodeSha256 = Convert.ToHexString(SHA256.HashData(code)), vectorsSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[1]))), results }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(new { passed = true, referenceOnly = true, cases = results.Count }));
}
finally { Native.VirtualFree(executable, 0, 0x8000); }

static class Native
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void Run(IntPtr block);
    [DllImport("kernel32", SetLastError = true)] internal static extern IntPtr VirtualAlloc(IntPtr address, nuint size, uint allocation, uint protection);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool VirtualProtect(IntPtr address, nuint size, uint protection, out uint old);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool VirtualFree(IntPtr address, nuint size, uint kind);
    [DllImport("kernel32")] internal static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool FlushInstructionCache(IntPtr process, IntPtr address, nuint length);
}
