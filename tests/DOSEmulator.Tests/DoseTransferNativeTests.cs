// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DOSEmulator.Contracts;
using DOSEmulator.Runtime;
using DOSEmulator.Runtime.Networking;

/// <summary>Explicit loopback-only DOSETF02 gateway probe; never registered in the common test suite.</summary>
internal static class DoseTransferNativeTests
{
    private static readonly byte[] Magic = "DOSETF02"u8.ToArray();
    private const string Code = "0011223344556677";

    /// <summary>Runs actual GET/FILE/ACK and GET/FILE/ABORT exchanges using one owned temporary file.</summary>
    public static int Run() => RunAsync().GetAwaiter().GetResult();

    /// <summary>Owns every test file and closes each loopback peer before the next transaction.</summary>
    private static async Task<int> RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dose-transfer-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "CHECK.TXT");
        byte[] expected = Encoding.ASCII.GetBytes("123456789");
        try
        {
            await File.WriteAllBytesAsync(path, expected).ConfigureAwait(false);
            CheckSnapshotGate(directory);
            await CheckGuestEpoch(path).ConfigureAwait(false);
            await Exchange(path, expected, 3, true).ConfigureAwait(false);
            await Exchange(path, expected, 4, false).ConfigureAwait(false);
            await Exchange(path, expected, 4, false, 5).ConfigureAwait(false);
            foreach (string unsafeName in new[] { "BAD NAME.TXT", ".hidden", "CON.TXT", "LPT1.TXT" })
            {
                using var rejectionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await DoseTransferGateway.ServeOneAsync(Path.Combine(directory, unsafeName), Code, rejectionDeadline.Token).ConfigureAwait(false); }
                catch (IOException) { continue; }
                throw new Exception("Unsafe transfer filename was accepted: " + unsafeName);
            }
            Console.WriteLine("PASS: DOSETF02 reset lease cancellation, wrong-code rejection, GET/FILE/ACK, ABORT/detail, exact bytes/CRC and unsafe-name rejection.");
            return 0;
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    /// <summary>Rejects full and media captures while a transfer could still publish from guest RAM.</summary>
    private static void CheckSnapshotGate(string directory)
    {
        using var session = new MachineSession(PcMachine.CreateDemo());
        var lease = session.GetTransferLease();
        if (!session.BeginTransfer(lease.Generation)) throw new Exception("Fresh transfer guard did not start.");
        // Reject both capture commands without creating a snapshot directory.
        void Rejected(string command)
        {
            string path = Path.Combine(directory, command);
            var pending = session.Enqueue(IpcProtocol.CreateRequest(command, new { path }));
            session.PumpCommands();
            if (pending.Result.Success || pending.Result.ErrorCode != "TransferSnapshotBlocked" || Directory.Exists(path))
                throw new Exception(command + " captured an in-flight import.");
        }
        Rejected("CreateSnapshot");
        Rejected("CreateMediaCheckpoint");
        session.MarkTransferPayload(lease.Generation);
        session.EndTransfer(lease.Generation, terminalReply: false);
        Rejected("CreateSnapshot");
        var reset = session.Enqueue(IpcProtocol.CreateRequest("HardwareReset"));
        session.PumpCommands();
        if (!reset.Result.Success || !session.BeginTransfer(session.GetTransferLease().Generation))
            throw new Exception("Guest reset did not clear the uncertain import gate.");
    }

    /// <summary>Checks that a queued host import cannot survive a real reset or PC replacement.</summary>
    private static async Task CheckGuestEpoch(string path)
    {
        using var session = new MachineSession(PcMachine.CreateDemo());
        var initial = session.GetTransferLease();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(initial.Token, deadline.Token);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> server = DoseTransferGateway.ServeOneAsync(path, Code, linked.Token, () => ready.TrySetResult());
        await ready.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        var reset = session.Enqueue(IpcProtocol.CreateRequest("HardwareReset"));
        session.PumpCommands();
        if (!reset.Result.Success || !initial.Token.IsCancellationRequested)
            throw new Exception("A reset retained a queued import.");
        try { await server.ConfigureAwait(false); throw new Exception("Reset listener accepted the retired import."); }
        catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
        var afterReset = session.GetTransferLease();
        if (afterReset.Generation == initial.Generation || afterReset.Token.IsCancellationRequested)
            throw new Exception("Reset import generation was not renewed.");
        var power = session.Enqueue(IpcProtocol.CreateRequest("PowerCycleDemo"));
        session.PumpCommands();
        if (!power.Result.Success || !afterReset.Token.IsCancellationRequested)
            throw new Exception("A replacement PC retained a queued import.");
    }

    /// <summary>Checks one complete bounded protocol response and sends the selected final acknowledgement.</summary>
    private static async Task Exchange(string path, byte[] expected, uint completion, bool accepted, uint detail = 0)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> server = DoseTransferGateway.ServeOneAsync(path, Code, stop.Token, () => ready.TrySetResult());
        await ready.Task.WaitAsync(stop.Token).ConfigureAwait(false);
        using (var wrong = new TcpClient())
        {
            await wrong.ConnectAsync(IPAddress.Loopback, 28595, stop.Token).ConfigureAwait(false);
            using NetworkStream rejected = wrong.GetStream();
            byte[] attempt = new byte[24]; Magic.CopyTo(attempt, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(attempt.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(attempt.AsSpan(12), 8);
            Convert.FromHexString("0011223344556676").CopyTo(attempt, 16);
            await rejected.WriteAsync(attempt, stop.Token).ConfigureAwait(false);
            if (await rejected.ReadAsync(new byte[1], stop.Token).ConfigureAwait(false) != 0 || server.IsCompleted)
                throw new Exception("Wrong import code consumed the transfer or received file data.");
        }
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, 28595, stop.Token).ConfigureAwait(false);
        using NetworkStream stream = client.GetStream();
        byte[] header = new byte[16];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 8);
        await stream.WriteAsync(header, stop.Token).ConfigureAwait(false);
        await stream.WriteAsync(Convert.FromHexString(Code), stop.Token).ConfigureAwait(false);
        await stream.ReadExactlyAsync(header, stop.Token).ConfigureAwait(false);
        if (!header.AsSpan(0, 8).SequenceEqual(Magic) || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) != 0)
            throw new Exception("DOSETF02 FILE header differs.");
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        if (length != 10 + "CHECK.TXT".Length + expected.Length) throw new Exception("DOSETF02 payload length differs.");
        byte[] payload = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(payload, stop.Token).ConfigureAwait(false);
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        int dataLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(2)));
        uint crc = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(6));
        if (nameLength != 9 || dataLength != expected.Length || crc != 0xcbf43926u ||
            !payload.AsSpan(10, nameLength).SequenceEqual("CHECK.TXT"u8) ||
            !payload.AsSpan(10 + nameLength).SequenceEqual(expected))
            throw new Exception("DOSETF02 filename, exact data or independent CRC oracle differs.");
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), completion);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), detail);
        await stream.WriteAsync(header, stop.Token).ConfigureAwait(false);
        if (detail != 0)
        {
            try { await server.ConfigureAwait(false); }
            catch (IOException error) when (error.Message.Contains($"stage {detail}", StringComparison.Ordinal)) { return; }
            throw new Exception("DOSETF02 guest failure detail was not reported.");
        }
        if (await server.ConfigureAwait(false) != accepted) throw new Exception("DOSETF02 completion result differs.");
    }

}
