using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DOSEmulator.Core;

namespace DOSEmulator.Runtime;

/// <summary>Private PCM16 stereo acceptance recording. Never overwrites an existing recording.</summary>
public sealed class PcmWaveRecorder : IDisposable
{
    private readonly FileStream _file;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _completed;
    public long Frames { get; private set; }
    public string? DataSha256 { get; private set; }
    public PcmWaveRecorder(string path)
    {
        _file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _file.Write(new byte[44]);
    }
    public void Write(AudioBlock block)
    {
        block.Validate();
        if (block.SampleRate != 48000 || block.Channels != 2) throw new ArgumentException("Expected 48-kHz stereo PCM.");
        byte[] bytes = new byte[block.Samples.Length * 2];
        for (int i = 0; i < block.Samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)Math.Round(Math.Clamp(block.Samples[i], -1f, 1f) * 32767));
        WritePcm16(bytes);
    }
    public void WritePcm16(ReadOnlySpan<byte> bytes)
    {
        if (_completed || bytes.Length % 4 != 0) throw new InvalidOperationException("Recording closed or incomplete stereo frame.");
        if ((ulong)(Frames * 4) + (uint)bytes.Length > uint.MaxValue - 36) throw new IOException("WAV size limit reached.");
        _file.Write(bytes); _hash.AppendData(bytes); Frames += bytes.Length / 4;
    }
    public void Complete()
    {
        if (_completed) return;
        _completed = true; uint length = checked((uint)(Frames * 4)); byte[] header = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), length + 36);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(header, 8); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1); BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), 48000); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), 192000);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), 4); BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(header, 36); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), length);
        _file.Position = 0; _file.Write(header); _file.Flush(true); DataSha256 = Convert.ToHexString(_hash.GetHashAndReset());
    }
    public void Dispose() { try { Complete(); } finally { _hash.Dispose(); _file.Dispose(); } }
}
