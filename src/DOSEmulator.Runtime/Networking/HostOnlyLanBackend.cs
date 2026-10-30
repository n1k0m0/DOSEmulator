// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading.Channels;
using DOSEmulator.Core;

namespace DOSEmulator.Runtime.Networking;

/// <summary>Isolated same-user Ethernet LAN shared across DOSE processes, with one host-only DHCP/loopback gateway per LAN.</summary>
public sealed class HostOnlyLanBackend : IEthernetBackend
{
    private const int Capacity = 256, MaximumBytes = 1600, MaximumPeers = 16;
    private static readonly ConcurrentDictionary<string, Hub> LocalHubs = new(StringComparer.Ordinal);
    private static readonly object CreationGate = new();
    private readonly Channel<byte[]> _send = Channel.CreateBounded<byte[]>(Capacity);
    private readonly ConcurrentQueue<byte[]> _receive = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly string _name;
    private NamedPipeClientStream? _pipe;
    private Hub? _localHub;
    private Task? _reader, _writer;
    private string? _error;
    private int _disposed, _receiveCount, _connected;
    /// <summary>Identifies the isolated LAN without advertising Internet access or a host virtual adapter.</summary>
    public string Description => "Host-only / " + _name + " / 10.0.2.0/24 (host 10.0.2.2)";
    /// <summary>A live pipe is required; another transport is never substituted after a hub failure.</summary>
    public bool IsConnected => Volatile.Read(ref _connected) != 0 && Volatile.Read(ref _disposed) == 0;
    /// <summary>Most recent connection, queue or transport failure.</summary>
    public string? Error => Volatile.Read(ref _error);

    /// <summary>Connects only on an explicit user action, with a bounded startup wait and current-user-only IPC.</summary>
    public HostOnlyLanBackend(string name, bool explicitlyConnect, string? nativeDirectory = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The host-only LAN requires Windows local pipes.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64) throw new ArgumentException("Invalid virtual LAN name.", nameof(name));
        _name = name;
        if (!explicitlyConnect) { _error = "Host-only networking is disconnected."; return; }
        string user = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("No Windows user identity.");
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user + "\n" + name)));
        string pipeName = "DOSE-Ethernet-1-" + key;
        try
        {
            for (int attempt = 0; attempt < 3 && _pipe is null; attempt++)
            {
                _pipe = Connect(pipeName, 150);
                if (_pipe is not null) break;
                Hub hub;
                lock (CreationGate)
                {
                    if (!LocalHubs.TryGetValue(pipeName, out hub!))
                    { hub = new Hub(pipeName, nativeDirectory); LocalHubs[pipeName] = hub; hub.Start(); }
                }
                if (!hub.Ready.Wait(TimeSpan.FromSeconds(12))) throw new IOException("The host-only gateway did not start.");
                if (hub.Error is not null) throw new IOException(hub.Error);
                if (hub.IsStopping) { hub.WaitForRetirement(); continue; }
                _pipe = Connect(pipeName, 3000);
            }
            if (_pipe is null) throw new IOException("The host-only LAN owner is unavailable.");
            LocalHubs.TryGetValue(pipeName, out _localHub);
            Volatile.Write(ref _connected, 1);
            _reader = Task.Run(ReadLoop); _writer = Task.Run(WriteLoop);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { _error = error.Message; _pipe?.Dispose(); _pipe = null; }
    }

    /// <summary>Attempts one bounded local-pipe connection without falling through to a network endpoint.</summary>
    private static NamedPipeClientStream? Connect(string name, int milliseconds)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            pipe.Connect(milliseconds);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            byte[] hello = Encoding.ASCII.GetBytes("DOSENET1");
            pipe.WriteAsync(hello, stop.Token).AsTask().GetAwaiter().GetResult();
            byte[] reply = new byte[hello.Length];
            pipe.ReadExactlyAsync(reply, stop.Token).AsTask().GetAwaiter().GetResult();
            if (!hello.AsSpan().SequenceEqual(reply)) throw new IOException("The host-only protocol version differs.");
            return pipe;
        }
        catch (Exception error) when (error is TimeoutException or IOException or OperationCanceledException) { pipe.Dispose(); return null; }
        catch { pipe.Dispose(); throw; }
    }

    /// <summary>Queues an owned frame or reports bounded drop-new backpressure.</summary>
    public void Send(ReadOnlySpan<byte> frame)
    {
        if (frame.Length is < 14 or > MaximumBytes) { Volatile.Write(ref _error, "Invalid Ethernet frame size."); return; }
        if (!IsConnected || !_send.Writer.TryWrite(frame.ToArray())) Volatile.Write(ref _error, "Host-only output is disconnected or full.");
    }

    /// <summary>Admits one owned complete frame to its machine owner without blocking.</summary>
    public bool TryReceive(out byte[] frame)
    {
        if (!IsConnected)
        {
            while (_receive.TryDequeue(out _)) Interlocked.Decrement(ref _receiveCount);
            frame = []; return false;
        }
        if (_receive.TryDequeue(out var value)) { Interlocked.Decrement(ref _receiveCount); frame = value; return true; }
        frame = []; return false;
    }

    /// <summary>Reads bounded framed packets; malformed lengths terminate this connection before allocation.</summary>
    private async Task ReadLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                byte[]? packet = await ReadPacket(_pipe!, _stop.Token).ConfigureAwait(false);
                if (packet is null) break;
                if (Interlocked.Increment(ref _receiveCount) <= Capacity) _receive.Enqueue(packet);
                else { Interlocked.Decrement(ref _receiveCount); Volatile.Write(ref _error, "Host-only receive queue full; new frame dropped."); }
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        { if (!_stop.IsCancellationRequested) Volatile.Write(ref _error, error.Message); }
        finally { Volatile.Write(ref _connected, 0); _stop.Cancel(); }
    }

    /// <summary>Writes only complete copied Ethernet packets and stops when the connection generation is retired.</summary>
    private async Task WriteLoop()
    {
        try
        {
            await foreach (byte[] frame in _send.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                await WritePacket(_pipe!, frame, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        { if (!_stop.IsCancellationRequested) Volatile.Write(ref _error, error.Message); }
        finally { Volatile.Write(ref _connected, 0); _stop.Cancel(); }
    }

    /// <summary>Reads one length-prefixed packet, distinguishing orderly EOF from a truncated transaction.</summary>
    private static async Task<byte[]?> ReadPacket(Stream stream, CancellationToken stop)
    {
        byte[] header = new byte[4];
        int first = await stream.ReadAsync(header.AsMemory(0, 1), stop).ConfigureAwait(false);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), stop).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is < 14 or > MaximumBytes) throw new InvalidDataException("Invalid host-only Ethernet packet length.");
        byte[] packet = new byte[length];
        await stream.ReadExactlyAsync(packet, stop).ConfigureAwait(false);
        return packet;
    }

    /// <summary>Serializes exactly one bounded Ethernet packet on a single writer task.</summary>
    private static async Task WritePacket(Stream stream, byte[] packet, CancellationToken stop)
    {
        byte[] header = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)packet.Length);
        await stream.WriteAsync(header, stop).ConfigureAwait(false);
        await stream.WriteAsync(packet, stop).ConfigureAwait(false);
    }

    /// <summary>Disconnects, cancels blocked operations and joins both tasks before releasing their queues.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _connected, 0); _stop.Cancel(); _send.Writer.TryComplete(); _pipe?.Dispose();
        Task.WhenAll(new[] { _reader, _writer }.OfType<Task>()).GetAwaiter().GetResult();
        while (_receive.TryDequeue(out _)) { } _receiveCount = 0;
        _localHub?.WaitForLastPeerRetirement(); _stop.Dispose();
    }

    /// <summary>One LAN owner holds a per-user mutex on its own thread and routes a single DHCP stack to all ports.</summary>
    private sealed class Hub
    {
        internal readonly ManualResetEventSlim Ready = new(false);
        private readonly ManualResetEventSlim _retired = new(false);
        internal string? Error;
        /// <summary>Whether a previous empty LAN is already shutting down.</summary>
        internal bool IsStopping => _stop.IsCancellationRequested;
        private readonly string _pipeName;
        private readonly string? _nativeDirectory;
        private readonly CancellationTokenSource _stop = new();
        private readonly object _gate = new();
        private readonly List<Peer> _peers = [];
        private IEthernetBackend? _gateway;
        private bool _hadPeers;
        private readonly Thread _owner;
        /// <summary>Starts one candidate owner; an existing process keeps ownership without being replaced.</summary>
        internal Hub(string pipeName, string? nativeDirectory)
        {
            _pipeName = pipeName; _nativeDirectory = nativeDirectory;
            _owner = new Thread(Run) { IsBackground = true, Name = "DOSE host-only LAN" };
        }
        /// <summary>Starts only after registration under the creation gate; a losing factory cannot become an untracked gateway.</summary>
        internal void Start() => _owner.Start();
        /// <summary>Owns the gateway and accept loop for exactly this local Ethernet domain.</summary>
        private void Run()
        {
            Mutex? mutex = null; bool held = false;
            Task? gatewayReader = null;
            try
            {
                mutex = new Mutex(false, "Local\\" + _pipeName);
                try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
                if (!held) return;
                _gateway = new SlirpEthernetBackend(true, true, _nativeDirectory);
                if (!_gateway.IsConnected) throw new IOException(_gateway.Error ?? "Host-only gateway failed.");
                gatewayReader = Task.Run(PumpGateway);
                while (!_stop.IsCancellationRequested)
                {
                    var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, MaximumPeers + 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
                    Ready.Set();
                    try { server.WaitForConnectionAsync(_stop.Token).GetAwaiter().GetResult(); }
                    catch { server.Dispose(); throw; }
                    lock (_gate)
                    {
                        if (_stop.IsCancellationRequested || _peers.Count >= MaximumPeers) server.Dispose();
                        else { var peer = new Peer(this, server); _peers.Add(peer); _hadPeers = true; peer.Start(); }
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { if (!_stop.IsCancellationRequested) Error = error.Message; }
            finally
            {
                Ready.Set(); _stop.Cancel();
                Peer[] peers; lock (_gate) peers = _peers.ToArray();
                foreach (var peer in peers) peer.Close();
                foreach (var peer in peers) peer.Join();
                gatewayReader?.GetAwaiter().GetResult(); _gateway?.Dispose();
                if (held) mutex?.ReleaseMutex(); mutex?.Dispose();
                LocalHubs.TryRemove(new KeyValuePair<string, Hub>(_pipeName, this)); _retired.Set();
            }
        }
        /// <summary>Joins a retiring previous owner before an immediate reconnect can select a new generation.</summary>
        internal void WaitForRetirement()
        {
            if (!_retired.Wait(TimeSpan.FromSeconds(10))) throw new IOException("The previous host-only LAN is still stopping.");
            _owner.Join();
        }
        /// <summary>Waits for last-peer EOF and native gateway cleanup, without stopping another connected guest.</summary>
        internal void WaitForLastPeerRetirement()
        {
            lock (_gate) if (_peers.Count > 1) return;
            if (_retired.Wait(TimeSpan.FromSeconds(2))) { _owner.Join(); return; }
            if (IsStopping) WaitForRetirement();
        }
        /// <summary>Publishes only complete gateway frames, with bounded per-port backpressure.</summary>
        private async Task PumpGateway()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    while (_gateway!.TryReceive(out var packet)) Broadcast(packet, null);
                    if (!_gateway!.IsConnected) { Error = _gateway.Error; _stop.Cancel(); break; }
                    await Task.Delay(5, _stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }
        /// <summary>Switches raw Ethernet including broadcasts/IPX; only the gateway filter decides which frames enter its IPv4 stack.</summary>
        private void Input(byte[] packet, Peer sender)
        {
            Broadcast(packet, sender); _gateway!.Send(packet);
        }
        /// <summary>Offers one immutable packet to every other LAN port without blocking the sender.</summary>
        private void Broadcast(byte[] packet, Peer? sender)
        { lock (_gate) foreach (var peer in _peers) if (!ReferenceEquals(peer, sender)) peer.Queue(packet); }
        /// <summary>Stops a now-empty LAN so no hidden host service survives its last connected guest.</summary>
        private void Remove(Peer peer)
        {
            lock (_gate) { _peers.Remove(peer); if (_hadPeers && _peers.Count == 0) _stop.Cancel(); }
        }
        /// <summary>Independent bounded output and parser for one same-user Ethernet port.</summary>
        private sealed class Peer(Hub owner, NamedPipeServerStream pipe)
        {
            private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(Capacity);
            private readonly CancellationTokenSource _stop = new();
            private Task? _reader, _writer;
            private readonly TaskCompletionSource<bool> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Starts the two serialized directions after registration in the owner's peer table.</summary>
            internal void Start() { _reader = Task.Run(Read); _writer = Task.Run(Write); }
            /// <summary>Offers the already-owned immutable packet; drop-new protects the LAN against a slow peer.</summary>
            internal void Queue(byte[] packet) => _queue.Writer.TryWrite(packet);
            /// <summary>Forwards complete peer frames and retires this port after EOF or malformed data.</summary>
            private async Task Read()
            {
                try
                {
                    using var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, owner._stop.Token);
                    handshake.CancelAfter(TimeSpan.FromSeconds(3));
                    byte[] hello = new byte[8];
                    await pipe.ReadExactlyAsync(hello, handshake.Token).ConfigureAwait(false);
                    if (!hello.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes("DOSENET1"))) throw new IOException("Invalid host-only protocol greeting.");
                    owner._stop.Token.ThrowIfCancellationRequested();
                    await pipe.WriteAsync(hello, handshake.Token).ConfigureAwait(false);
                    _hello.TrySetResult(true);
                    while (!_stop.IsCancellationRequested && await ReadPacket(pipe, _stop.Token).ConfigureAwait(false) is { } packet) owner.Input(packet, this);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
                finally { Close(); owner.Remove(this); }
            }
            /// <summary>Completes one whole pipe write at a time, without borrowing machine memory.</summary>
            private async Task Write()
            {
                try
                {
                    if (!await _hello.Task.ConfigureAwait(false)) return;
                    await foreach (var packet in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false)) await WritePacket(pipe, packet, _stop.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
                finally { Close(); }
            }
            /// <summary>Cancels blocked pipe operations and releases the connected handle once.</summary>
            internal void Close() { _hello.TrySetResult(false); _stop.Cancel(); _queue.Writer.TryComplete(); pipe.Dispose(); }
            /// <summary>Joins reader and writer after the hub has retired their handles.</summary>
            internal void Join() => Task.WhenAll(new[] { _reader, _writer }.OfType<Task>()).GetAwaiter().GetResult();
        }
    }
}
