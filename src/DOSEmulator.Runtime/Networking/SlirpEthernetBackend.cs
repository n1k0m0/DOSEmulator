// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DOSEmulator.Core;

namespace DOSEmulator.Runtime.Networking;

/// <summary>Real libslirp IPv4 DHCP/ARP/DNS/TCP/UDP transport with host-only ingress restrictions and bounded owned queues.</summary>
public sealed class SlirpEthernetBackend : IEthernetBackend
{
    public const int MaximumFrames = 256, MaximumFrameBytes = 1600;
    private const int MaximumSockets = 1024;
    private readonly bool _hostOnly;
    private readonly byte _subnet;
    private readonly string? _directory;
    private readonly object _queueGate = new();
    private readonly Queue<byte[]> _send = new(), _receive = new();
    private readonly Queue<ForwardRequest> _forwards = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread? _worker;
    private int _stop, _connected, _disposed;
    private string? _error;
    private readonly List<NativeSlirp.PollFd> _poll = [];
    private NativeSlirp.PollFd[] _polled = [];
    private readonly Dictionary<nint, Timer> _timers = [];
    private long _nextTimer;
    private string? _callbackFailure;

    /// <summary>Explicit transient forwarding request, completed only by the native stack's worker and never saved with the PC.</summary>
    private sealed record ForwardRequest(bool Remove, bool Udp, int HostPort, uint GuestAddress, int GuestPort, TaskCompletionSource<bool> Completion);

    /// <summary>One native timer token; deadlines use the shared external-stack host clock.</summary>
    private sealed class Timer(int id, nint opaque)
    {
        internal readonly int Id = id;
        internal readonly nint Opaque = opaque;
        internal long Expires = long.MaxValue;
    }

    /// <summary>English transport state; host-only never promises Internet access.</summary>
    public string Description => (_hostOnly ? "Host-only" : "NAT") + $" / libslirp IPv4 / 10.0.{_subnet}.0/24";
    /// <summary>The native stack is connected only after explicit successful worker initialization.</summary>
    public bool IsConnected => Volatile.Read(ref _connected) != 0 && Volatile.Read(ref _disposed) == 0;
    /// <summary>Transport failure or bounded queue rejection; no alternative mode is silently selected.</summary>
    public string? Error => Volatile.Read(ref _error);

    /// <summary>No native code or endpoint is opened unless the caller explicitly supplies true.</summary>
    public SlirpEthernetBackend(bool hostOnly, bool explicitlyConnect, string? nativeDirectory = null, int subnetIndex = 2)
    {
        if (subnetIndex is < 2 or > 253) throw new ArgumentOutOfRangeException(nameof(subnetIndex));
        _hostOnly = hostOnly; _directory = nativeDirectory; _subnet = (byte)subnetIndex;
        if (!explicitlyConnect) { _error = "Networking is disconnected. Select Connect to activate this transport."; return; }
        _worker = new Thread(Work) { IsBackground = true, Name = hostOnly ? "DOSE host-only IPv4" : "DOSE NAT IPv4" };
        _worker.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
        {
            Volatile.Write(ref _error, "The native IPv4 transport did not finish initialization.");
            Dispose();
        }
    }

    /// <summary>Copies one complete frame to the host worker, reporting invalid sizes and drop-new queue overflow.</summary>
    public void Send(ReadOnlySpan<byte> frame)
    {
        if (!IsConnected) { Volatile.Write(ref _error, "The IPv4 transport is not connected."); return; }
        if (frame.Length is < 14 or > MaximumFrameBytes) { Volatile.Write(ref _error, "The Ethernet frame length is outside14..1600 bytes."); return; }
        if (_hostOnly && !HostOnlyFrameAllowed(frame, _subnet)) { Volatile.Write(ref _error, "Host-only networking blocked a destination outside its virtual gateway/DHCP scope."); return; }
        lock (_queueGate)
        {
            if (!IsConnected) return;
            if (_send.Count >= MaximumFrames) { Volatile.Write(ref _error, "The IPv4 send queue is full; the new frame was dropped."); return; }
            _send.Enqueue(frame.ToArray()); _wake.Set();
        }
    }

    /// <summary>Explicitly bind one localhost-only TCP/UDP port; no automatic or wildcard/public forwarding exists.</summary>
    public Task<bool> AddLoopbackForwardAsync(int hostPort, string guestAddress, int guestPort, bool udp = false)
    {
        if (hostPort is < 1 or > 65535 || guestPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(hostPort));
        if (!System.Net.IPAddress.TryParse(guestAddress, out var address)) throw new ArgumentException("Use one guest IPv4 address in this virtual subnet.", nameof(guestAddress));
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4 || bytes[0] != 10 || bytes[1] != 0 || bytes[2] != _subnet || bytes[3] is < 15 or > 254)
            throw new ArgumentException("A forward must target an assigned guest IPv4 address in this virtual subnet.", nameof(guestAddress));
        return QueueForward(false, udp, hostPort, Address(bytes[0], bytes[1], bytes[2], bytes[3]), guestPort);
    }

    /// <summary>Remove one exact localhost TCP/UDP listener; its mapping is never restored after reconnect or snapshot import.</summary>
    public Task<bool> RemoveLoopbackForwardAsync(int hostPort, bool udp = false)
    {
        if (hostPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(hostPort));
        return QueueForward(true, udp, hostPort, 0, 0);
    }

    /// <summary>Transfer a bounded explicit control request to the worker, without calling native code on the machine owner.</summary>
    private Task<bool> QueueForward(bool remove, bool udp, int hostPort, uint address, int guestPort)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_queueGate)
        {
            if (!IsConnected || _forwards.Count >= 64)
            { Volatile.Write(ref _error, "The IPv4 transport is disconnected or its explicit forwarding queue is full."); result.SetResult(false); }
            else { _forwards.Enqueue(new(remove, udp, hostPort, address, guestPort, result)); _wake.Set(); }
        }
        return result.Task;
    }

    /// <summary>Transfers a complete owned frame without waiting, and never exposes the native buffer.</summary>
    public bool TryReceive(out byte[] frame)
    {
        lock (_queueGate)
        {
            if (IsConnected && _receive.TryDequeue(out frame!)) return true;
        }
        frame = []; return false;
    }

    /// <summary>Allow only virtual-subnet ARP, DHCP and plain IPv4 to10.0.2.2 (libslirp's loopback alias).</summary>
    public static bool HostOnlyFrameAllowed(ReadOnlySpan<byte> frame, int subnetIndex = 2)
    {
        if (subnetIndex is < 2 or > 253 || frame.Length is < 14 or > MaximumFrameBytes) return false;
        ushort type = BinaryPrimitives.ReadUInt16BigEndian(frame[12..]);
        if (type == 0x0806)
        {
            if (frame.Length < 42 || BinaryPrimitives.ReadUInt16BigEndian(frame[14..]) != 1 ||
                BinaryPrimitives.ReadUInt16BigEndian(frame[16..]) != 0x0800 || frame[18] != 6 || frame[19] != 4) return false;
            return frame[38] == 10 && frame[39] == 0 && frame[40] == subnetIndex;
        }
        if (type != 0x0800 || frame.Length < 34 || frame[14] != 0x45) return false; // No IPv6, VLAN or source-route options.
        int length = BinaryPrimitives.ReadUInt16BigEndian(frame[16..]);
        if (length < 20 || length > frame.Length - 14) return false;
        var destination = frame.Slice(30, 4);
        if (destination[0] == 10 && destination[1] == 0 && destination[2] == subnetIndex && destination[3] == 2) return true;
        bool broadcast = destination.SequenceEqual(new byte[] { 255, 255, 255, 255 }) ||
            destination[0] == 10 && destination[1] == 0 && destination[2] == subnetIndex && destination[3] == 255;
        if (!broadcast || frame[23] != 17 || length < 28 || (BinaryPrimitives.ReadUInt16BigEndian(frame[20..]) & 0x3fff) != 0) return false;
        return BinaryPrimitives.ReadUInt16BigEndian(frame[34..]) == 68 && BinaryPrimitives.ReadUInt16BigEndian(frame[36..]) == 67;
    }

    /// <summary>One native-thread owner; no scheduler callback or serialized PC field can call libslirp directly.</summary>
    private void Work()
    {
        NativeSlirp? native = null; nint slirp = 0, callbacksMemory = 0;
        // Every delegate remains rooted through native cleanup; callbacks never throw across the C ABI.
        NativeSlirp.WriteCallback output = Output;
        NativeSlirp.ErrorCallback guestError = (_, _) => Volatile.Write(ref _error, "libslirp rejected an invalid guest packet.");
        NativeSlirp.ClockCallback clock = _ => NativeSlirp.Nanoseconds();
        NativeSlirp.TimerFreeCallback free = (timer, _) => _timers.Remove(timer);
        NativeSlirp.TimerModCallback modify = (timer, expires, _) => { if (_timers.TryGetValue(timer, out var value)) value.Expires = expires; };
        NativeSlirp.TimerNewCallback create = (id, opaque, _) => NewTimer(id, opaque);
        NativeSlirp.FdCallback fd = (_, _) => { };
        NativeSlirp.SocketCallback socket = (_, _) => { };
        NativeSlirp.NotifyCallback notify = _ => _wake.Set();
        NativeSlirp.InitCallback initialized = (_, _) => { };
        NativeSlirp.AddPollCallback add = AddPoll;
        NativeSlirp.GetEventsCallback events = GetEvents;
        try
        {
            lock (NativeSlirp.Gate)
            {
                native = new NativeSlirp(_directory);
                var callbacks = new NativeSlirp.Callbacks
                {
                    SendPacket = Marshal.GetFunctionPointerForDelegate(output), GuestError = Marshal.GetFunctionPointerForDelegate(guestError),
                    ClockGetNs = Marshal.GetFunctionPointerForDelegate(clock), TimerFree = Marshal.GetFunctionPointerForDelegate(free),
                    TimerMod = Marshal.GetFunctionPointerForDelegate(modify), TimerNewOpaque = Marshal.GetFunctionPointerForDelegate(create),
                    RegisterFd = Marshal.GetFunctionPointerForDelegate(fd), UnregisterFd = Marshal.GetFunctionPointerForDelegate(fd),
                    RegisterSocket = Marshal.GetFunctionPointerForDelegate(socket), UnregisterSocket = Marshal.GetFunctionPointerForDelegate(socket),
                    Notify = Marshal.GetFunctionPointerForDelegate(notify), InitCompleted = Marshal.GetFunctionPointerForDelegate(initialized)
                };
                callbacksMemory = Marshal.AllocHGlobal(Marshal.SizeOf<NativeSlirp.Callbacks>());
                Marshal.StructureToPtr(callbacks, callbacksMemory, false);
                var config = new NativeSlirp.Config
                {
                    Version = 6, Restricted = 0, Ipv4 = 1, Ipv6 = 0,
                    Network = Address(10, 0, _subnet, 0), Netmask = Address(255, 255, 255, 0), Host = Address(10, 0, _subnet, 2),
                    DhcpStart = Address(10, 0, _subnet, 15), Nameserver = _hostOnly ? 0 : Address(10, 0, _subnet, 3),
                    Mtu = 1500, Mru = 1500, DisableDns = _hostOnly ? (byte)1 : (byte)0,
                    DisableDhcp = 0, DisableHostLoopback = 0, EnableUnsafeEmulation = 0
                };
                slirp = native.New(ref config, callbacksMemory, 0);
                if (slirp == 0) throw new InvalidOperationException("libslirp could not create the selected IPv4 stack.");
            }
            if (Volatile.Read(ref _stop) == 0) Volatile.Write(ref _connected, 1);
            _ready.Set();
            while (Volatile.Read(ref _stop) == 0)
            {
                ProcessForwards(native, slirp);
                int sent = 0;
                while (Volatile.Read(ref _stop) == 0 && sent++ < MaximumFrames && TakeSend(out var frame))
                    lock (NativeSlirp.Gate) native.Input(slirp, frame, frame.Length);
                uint timeout = 20;
                lock (NativeSlirp.Gate)
                {
                    FireTimers(native, slirp);
                    _poll.Clear(); native.Fill(slirp, ref timeout, add, 0);
                }
                if (_callbackFailure is not null) throw new InvalidOperationException(_callbackFailure);
                _polled = _poll.ToArray();
                int milliseconds = (int)Math.Min(timeout, 20);
                int polled;
                if (_polled.Length == 0) { _wake.WaitOne(milliseconds); polled = 0; }
                else polled = NativeSlirp.WSAPoll(_polled, (uint)_polled.Length, milliseconds);
                if (Volatile.Read(ref _stop) != 0) break;
                if (polled < 0) throw new IOException("Windows socket polling failed (" + NativeSlirp.LastSocketError() + ").");
                lock (NativeSlirp.Gate) native.Poll(slirp, polled < 0 ? 1 : 0, events, 0);
                if (_callbackFailure is not null) throw new InvalidOperationException(_callbackFailure);
            }
        }
        catch (Exception error) { Volatile.Write(ref _error, "IPv4 transport failed: " + error.Message); }
        finally
        {
            Volatile.Write(ref _connected, 0); _ready.Set();
            lock (NativeSlirp.Gate)
            {
                if (slirp != 0) native?.Cleanup(slirp);
                if (callbacksMemory != 0) Marshal.FreeHGlobal(callbacksMemory);
                native?.Dispose();
            }
            _timers.Clear();
            lock (_queueGate)
            {
                _send.Clear(); _receive.Clear();
                while (_forwards.TryDequeue(out var request)) request.Completion.TrySetResult(false);
            }
            GC.KeepAlive(output); GC.KeepAlive(guestError); GC.KeepAlive(clock); GC.KeepAlive(free); GC.KeepAlive(modify);
            GC.KeepAlive(create); GC.KeepAlive(fd); GC.KeepAlive(socket); GC.KeepAlive(notify); GC.KeepAlive(initialized);
            GC.KeepAlive(add); GC.KeepAlive(events);
        }
    }

    /// <summary>Construct a network-order in_addr in the little-endian Windows struct representation.</summary>
    private static uint Address(byte a, byte b, byte c, byte d) => (uint)(a | b << 8 | c << 16 | d << 24);

    /// <summary>Removes a queued copied frame without holding the queue lock during native execution.</summary>
    private bool TakeSend(out byte[] frame)
    { lock (_queueGate) return _send.TryDequeue(out frame!); }

    /// <summary>Activate only explicitly queued localhost listeners while holding the common native-library gate.</summary>
    private void ProcessForwards(NativeSlirp native, nint slirp)
    {
        for (int i = 0; i < 64; i++)
        {
            ForwardRequest request;
            lock (_queueGate) { if (!_forwards.TryDequeue(out request!)) return; }
            try
            {
                int result;
                lock (NativeSlirp.Gate)
                {
                    if (Volatile.Read(ref _stop) != 0) { request.Completion.TrySetResult(false); return; }
                    result = request.Remove ? native.RemoveForward(slirp, request.Udp ? 1 : 0, Address(127, 0, 0, 1), request.HostPort) :
                        native.AddForward(slirp, request.Udp ? 1 : 0, Address(127, 0, 0, 1), request.HostPort, request.GuestAddress, request.GuestPort);
                }
                if (result != 0) Volatile.Write(ref _error, "The explicit localhost port forward could not be added or removed.");
                request.Completion.TrySetResult(result == 0);
            }
            catch { request.Completion.TrySetResult(false); throw; }
        }
    }

    /// <summary>Copies the native callback buffer, dropping new frames at the documented bound.</summary>
    private nint Output(nint buffer, nuint length, nint opaque)
    {
        try
        {
            if (length is < 14 or > MaximumFrameBytes || buffer == 0) return (nint)length;
            var bytes = new byte[(int)length]; Marshal.Copy(buffer, bytes, 0, bytes.Length);
            lock (_queueGate)
            {
                if (Volatile.Read(ref _stop) == 0 && _receive.Count < MaximumFrames) _receive.Enqueue(bytes);
                else Volatile.Write(ref _error, "The IPv4 receive queue is full or disconnected; the new frame was dropped.");
            }
            return (nint)length;
        }
        catch { _callbackFailure = "The native IPv4 output callback could not publish a bounded frame."; return -1; }
    }

    /// <summary>Creates a bounded opaque timer token without allocating a managed callback in guest state.</summary>
    private nint NewTimer(int id, nint opaque)
    {
        try
        {
            if (id != 0 || _timers.Count >= 16 || _nextTimer == long.MaxValue) { _callbackFailure = "Invalid native IPv4 timer request."; return 0; }
            nint token = (nint)(++_nextTimer); _timers.Add(token, new(id, opaque)); return token;
        }
        catch { _callbackFailure = "The native IPv4 timer could not be allocated."; return 0; }
    }

    /// <summary>Fires a finite snapshot of due native timers; callbacks may remove or rearm their own token.</summary>
    private void FireTimers(NativeSlirp native, nint slirp)
    {
        long now = NativeSlirp.Nanoseconds() / 1_000_000;
        foreach (var timer in _timers.Values.Where(value => value.Expires <= now).ToArray())
        { timer.Expires = long.MaxValue; native.HandleTimer(slirp, timer.Id, timer.Opaque); }
    }

    /// <summary>Collects pointer-sized sockets and translates libslirp readiness flags to Windows poll events.</summary>
    private int AddPoll(nuint socket, int events, nint opaque)
    {
        try
        {
            if (_poll.Count >= MaximumSockets) { _callbackFailure = "The native IPv4 socket poll set exceeded1024 entries."; return -1; }
            short requested = WindowsPollEvents(events);
            int index = _poll.Count; _poll.Add(new() { Socket = socket, Events = requested }); return index;
        }
        catch { _callbackFailure = "The native IPv4 poll callback failed."; return -1; }
    }

    /// <summary>Reports only the matching completed Windows poll result to libslirp.</summary>
    private int GetEvents(int index, nint opaque)
    {
        if ((uint)index >= (uint)_polled.Length) return 0;
        short returned = _polled[index].Returned;
        return SlirpPollEvents(returned);
    }

    /// <summary>Maps ordinary and urgent readiness to supported Winsock flags; Microsoft WSAPoll rejects POLLPRI.</summary>
    public static short WindowsPollEvents(int events) => (short)(((events & 1) != 0 ? 0x0100 : 0) |
        ((events & 2) != 0 ? 0x0010 : 0) | ((events & 4) != 0 ? 0x0200 : 0));

    /// <summary>Maps normal, out-of-band, error, invalid-handle and hangup results back to independent libslirp flags.</summary>
    public static int SlirpPollEvents(short events) => ((events & 0x0100) != 0 ? 1 : 0) |
        ((events & 0x0010) != 0 ? 2 : 0) | ((events & 0x0200) != 0 ? 4 : 0) |
        ((events & (0x0001 | 0x0004)) != 0 ? 8 : 0) | ((events & 0x0002) != 0 ? 16 : 0);

    /// <summary>Stops and joins the single worker before disposing its wait handles or discarding external queues.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _stop, 1); Volatile.Write(ref _connected, 0); _wake.Set();
        _worker?.Join();
        lock (_queueGate)
        {
            _send.Clear(); _receive.Clear();
            while (_forwards.TryDequeue(out var request)) request.Completion.TrySetResult(false);
            _ready.Dispose(); _wake.Dispose();
        }
    }
}
