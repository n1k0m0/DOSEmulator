// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Runtime.InteropServices;
using DOSEmulator.Core;

namespace DOSEmulator.Runtime.Networking;

/// <summary>Explicit local Ethernet bridge through installed Npcap, bounded copied queues and one nonblocking capture worker.</summary>
public sealed class NpcapEthernetBackend : IEthernetBackend
{
    private readonly object _queueGate = new();
    private readonly Queue<byte[]> _send = new(), _receive = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly string _adapter;
    private readonly byte[] _mac;
    private readonly Thread? _worker;
    private int _stop, _connected, _disposed;
    private string? _error;

    /// <summary>Names the explicitly selected local interface; never suggests a fallback interface.</summary>
    public string Description => "Bridged / Npcap / " + _adapter;
    /// <summary>A live DLT_EN10MB handle with an installed guest-destination filter has actually been activated.</summary>
    public bool IsConnected => Volatile.Read(ref _connected) != 0 && Volatile.Read(ref _disposed) == 0;
    /// <summary>Capture/driver/queue failure presented in English without silently changing network mode.</summary>
    public string? Error => Volatile.Read(ref _error);

    /// <summary>No driver/device/DLL operation occurs unless explicitlyConnect is true.</summary>
    public NpcapEthernetBackend(string adapter, string macAddress, bool explicitlyConnect)
    {
        _adapter = adapter; _mac = NetworkBackendFactory.ParseMacAddress(macAddress);
        if (!explicitlyConnect) { _error = "Bridged networking is disconnected. Select an adapter and Connect."; return; }
        if (!NativePcap.ValidAdapterName(adapter)) { _error = "Choose one explicit local Npcap GUID adapter for bridged networking."; return; }
        _worker = new Thread(Work) { IsBackground = true, Name = "DOSE Npcap Ethernet" }; _worker.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
        { Volatile.Write(ref _error, "Npcap did not finish opening the selected adapter."); Dispose(); }
    }

    /// <summary>Copies one bounded guest frame for transmission by the capture's sole owning worker.</summary>
    public void Send(ReadOnlySpan<byte> frame)
    {
        if (!IsConnected) { Volatile.Write(ref _error, "The bridged transport is not connected."); return; }
        if (frame.Length is < 14 or > SlirpEthernetBackend.MaximumFrameBytes)
        { Volatile.Write(ref _error, "The Ethernet frame length is outside14..1600 bytes."); return; }
        lock (_queueGate)
        {
            if (!IsConnected) return;
            if (_send.Count >= SlirpEthernetBackend.MaximumFrames)
            { Volatile.Write(ref _error, "The bridge send queue is full; the new frame was dropped."); return; }
            _send.Enqueue(frame.ToArray()); _wake.Set();
        }
    }

    /// <summary>Returns a complete owned frame immediately, with no native buffer exposed to the machine owner.</summary>
    public bool TryReceive(out byte[] frame)
    {
        lock (_queueGate) { if (IsConnected && _receive.TryDequeue(out frame!)) return true; }
        frame = []; return false;
    }

    /// <summary>Only this worker calls the selected capture handle; nonblocking reads make shutdown independent of capture timeouts.</summary>
    private void Work()
    {
        NativePcap? native = null; nint capture = 0;
        try
        {
            native = new NativePcap();
            if (!native.Adapters().Any(value => value.Name == _adapter)) throw new InvalidOperationException("The selected Npcap adapter is no longer present. Select it again; no other adapter was opened.");
            byte[] error = new byte[NativePcap.ErrorBytes];
            capture = native.Open(_adapter, SlirpEthernetBackend.MaximumFrameBytes, 1, 20, error);
            if (capture == 0) throw new InvalidOperationException("Npcap could not open the selected adapter. Check driver access and adapter availability.");
            if (native.Link(capture) != 1) throw new InvalidOperationException("The selected Npcap adapter does not expose Ethernet (DLT_EN10MB). Choose a supported Ethernet adapter.");
            if (native.Nonblock(capture, 1, error) != 0) throw new InvalidOperationException("Npcap could not make this capture nonblocking; the adapter was closed.");
            string mac = string.Join(':', _mac.Select(value => value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
            var filter = new NativePcap.FilterProgram();
            if (native.Compile(capture, ref filter, "ether dst " + mac + " or ether broadcast or ether multicast", 1, uint.MaxValue) != 0)
                throw new InvalidOperationException("Npcap could not compile the bounded guest Ethernet filter.");
            try { if (native.SetFilter(capture, ref filter) != 0) throw new InvalidOperationException("Npcap could not install the guest Ethernet filter."); }
            finally { native.FreeFilter(ref filter); }
            if (Volatile.Read(ref _stop) == 0) Volatile.Write(ref _connected, 1);
            _ready.Set();
            while (Volatile.Read(ref _stop) == 0)
            {
                for (int i = 0; Volatile.Read(ref _stop) == 0 && i < SlirpEthernetBackend.MaximumFrames && TakeSend(out var frame); i++)
                    if (native.Send(capture, frame, frame.Length) != 0) throw new InvalidOperationException("Npcap failed to send a complete guest Ethernet frame.");
                for (int i = 0; Volatile.Read(ref _stop) == 0 && i < SlirpEthernetBackend.MaximumFrames; i++)
                {
                    int status = native.Next(capture, out nint headerPointer, out nint bytes);
                    if (status == 0) break;
                    if (status != 1) throw new InvalidOperationException("Npcap capture ended or failed; the bridge was disconnected.");
                    if (headerPointer == 0 || bytes == 0) throw new InvalidOperationException("Npcap returned an invalid packet buffer.");
                    var header = Marshal.PtrToStructure<NativePcap.PacketHeader>(headerPointer);
                    if (header.CapturedBytes != header.OriginalBytes || header.CapturedBytes is < 14 or > SlirpEthernetBackend.MaximumFrameBytes) continue;
                    byte[] received = new byte[(int)header.CapturedBytes]; Marshal.Copy(bytes, received, 0, received.Length);
                    if (!SelectedDestination(received) || received.AsSpan(6, 6).SequenceEqual(_mac)) continue;
                    lock (_queueGate)
                    {
                        if (Volatile.Read(ref _stop) == 0 && _receive.Count < SlirpEthernetBackend.MaximumFrames) _receive.Enqueue(received);
                        else Volatile.Write(ref _error, "The bridge receive queue is full or disconnected; the new frame was dropped.");
                    }
                }
                _wake.WaitOne(2);
            }
        }
        catch (Exception error) { Volatile.Write(ref _error, "Bridged transport failed: " + error.Message); }
        finally
        {
            Volatile.Write(ref _connected, 0); _ready.Set();
            if (capture != 0) native?.Close(capture);
            native?.Dispose();
            lock (_queueGate) { _send.Clear(); _receive.Clear(); }
        }
    }

    /// <summary>Only this guest's unicast and Ethernet group destinations can reach its bounded receive queue.</summary>
    private bool SelectedDestination(ReadOnlySpan<byte> frame) => (frame[0] & 1) != 0 || frame[..6].SequenceEqual(_mac);

    /// <summary>Release the queue lock before entering the native sender.</summary>
    private bool TakeSend(out byte[] frame) { lock (_queueGate) return _send.TryDequeue(out frame!); }

    /// <summary>Signal and join the nonblocking worker before closing wait handles or discarding host-side frames.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _stop, 1); Volatile.Write(ref _connected, 0); _wake.Set();
        _worker?.Join();
        lock (_queueGate) { _send.Clear(); _receive.Clear(); _ready.Dispose(); _wake.Dispose(); }
    }
}
