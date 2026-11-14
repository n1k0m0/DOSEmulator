// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DOSEmulator.Runtime.Networking;

/// <summary>One explicit, loopback-only DOSE Tools import transaction; never owns guest RAM or a VM snapshot.</summary>
public static class DoseTransferGateway
{
    /// <summary>Eight-byte protocol identifier shared with the Windows 9x import helper.</summary>
    private static ReadOnlySpan<byte> Magic => "DOSETF02"u8;
    /// <summary>Creates one unpredictable 64-bit code shown to the user only for this explicit import.</summary>
    public static string CreateCode() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    /// <summary>Serves one selected host file to an already running Windows guest over the existing NAT alias.</summary>
    public static async Task<bool> ServeOneAsync(string selectedFile, string code, CancellationToken cancellationToken = default,
        Action? onReady = null, Action? onPayloadStarting = null, Action? onTerminalReply = null)
    {
        if (code.Length != 16 || !code.All(Uri.IsHexDigit))
            throw new ArgumentException("The import code must contain exactly 16 hexadecimal characters.", nameof(code));
        byte[] expectedCode = Convert.FromHexString(code);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        CancellationToken stop = deadline.Token;
        string path = Path.GetFullPath(selectedFile);
        string name = Path.GetFileName(path);
        if (name.Length is < 1 or > 64 || !name.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.'))
            throw new IOException("Select a file with a simple ASCII name of at most 64 characters.");
        string stem = name.Split('.')[0].ToUpperInvariant();
        if (name.StartsWith('.') || name.EndsWith('.') || stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9')
            throw new IOException("The selected filename is reserved.");
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > 60000)
            throw new IOException("Select an ordinary file of at most 60000 bytes.");
        byte[] data = await File.ReadAllBytesAsync(path, stop).ConfigureAwait(false);
        if (data.Length > 60000 || data.Length != info.Length) throw new IOException("The selected file changed while preparing the transfer.");
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        byte[] payload = new byte[10 + nameBytes.Length + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2), (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(6), Crc32(data));
        nameBytes.CopyTo(payload, 10); data.CopyTo(payload, 10 + nameBytes.Length);
        var listener = new TcpListener(IPAddress.Loopback, 28595);
        listener.Start(1);
        try
        {
            onReady?.Invoke();
            TcpClient? accepted = null;
            while (accepted is null)
            {
                TcpClient candidate = await listener.AcceptTcpClientAsync(stop).ConfigureAwait(false);
                using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                requestDeadline.CancelAfter(TimeSpan.FromSeconds(30));
                byte[] requestHeader = new byte[16], suppliedCode = new byte[8];
                try
                {
                    NetworkStream request = candidate.GetStream();
                    await request.ReadExactlyAsync(requestHeader, requestDeadline.Token).ConfigureAwait(false);
                    if (!requestHeader.AsSpan(0, 8).SequenceEqual(Magic) ||
                        BinaryPrimitives.ReadUInt32LittleEndian(requestHeader.AsSpan(8)) != 1 ||
                        BinaryPrimitives.ReadUInt32LittleEndian(requestHeader.AsSpan(12)) != 8)
                        continue;
                    await request.ReadExactlyAsync(suppliedCode, requestDeadline.Token).ConfigureAwait(false);
                    if (CryptographicOperations.FixedTimeEquals(suppliedCode, expectedCode)) accepted = candidate;
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested) { continue; }
                catch (EndOfStreamException) { continue; }
                finally { if (accepted is null) candidate.Dispose(); }
            }
            using TcpClient client = accepted;
            using NetworkStream stream = client.GetStream();
            byte[] header = new byte[16];
            Magic.CopyTo(header); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)payload.Length);
            await stream.WriteAsync(header, stop).ConfigureAwait(false);
            onPayloadStarting?.Invoke();
            await stream.WriteAsync(payload, stop).ConfigureAwait(false);
            await stream.ReadExactlyAsync(header, stop).ConfigureAwait(false);
            if (!header.AsSpan(0, 8).SequenceEqual(Magic))
                throw new InvalidDataException("Invalid DOSE Tools completion response.");
            uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            uint detail = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
            if (opcode == 4 || opcode == 3 && detail == 0) onTerminalReply?.Invoke();
            if (opcode == 4 && detail != 0)
                throw new IOException($"Windows guest import aborted at stage {detail}.");
            if (detail != 0) throw new InvalidDataException("Invalid DOSE Tools completion detail.");
            return opcode switch
            {
                3 => true,
                4 => false,
                _ => throw new InvalidDataException("Invalid DOSE Tools completion opcode.")
            };
        }
        finally { listener.Stop(); }
    }

    /// <summary>Computes the protocol's CRC32/IEEE over the exact selected payload.</summary>
    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
        }
        return ~crc;
    }
}
